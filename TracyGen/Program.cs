using CppAst;
using System;
using System.IO;

namespace TracyGen
{
	class Program
	{
		static int Main(string[] args)
		{
			var headerFile = Path.Combine(AppContext.BaseDirectory, "Headers", "tracy_capi.h");

			if (!File.Exists(headerFile))
			{
				Console.Error.WriteLine($"Header not found: {headerFile}");
				return 1;
			}

			var options = new CppParserOptions
			{
				ParseMacros = true,
			};

			// tracy_capi.h includes "tracy/TracyC.h", which in turn includes
			// "../common/TracyApi.h", so the folder holding both subdirectories is the root.
			options.IncludeFolders.Add(Path.Combine(AppContext.BaseDirectory, "Headers"));

			// Stand-ins for <stddef.h> and <stdint.h>. libclang ships freestanding headers but
			// no libc, so without these the parse fails on a bare Linux runner and succeeds on
			// Windows — the exact trap the fleet playbook documents.
			options.IncludeFolders.Add(Path.Combine(AppContext.BaseDirectory, "Headers", "libc-stubs"));

			var compilation = CppParser.ParseFile(headerFile, options);

			if (compilation.HasErrors)
			{
				foreach (var message in compilation.Diagnostics.Messages)
				{
					if (message.Type == CppLogMessageType.Error)
					{
						Console.Error.WriteLine(message);
					}
				}

				return 1;
			}

			var outputPath = ResolveOutputPath();
			if (outputPath == null)
			{
				Console.Error.WriteLine("Could not locate the Evergine.Bindings.Tracy project folder.");
				return 1;
			}

			Directory.CreateDirectory(outputPath);

			int functionCount = CsCodeGenerator.Instance.Generate(compilation, outputPath);

			// TracyC.h without TRACY_ENABLE declares no functions at all — an empty result here
			// means the define was lost, not that upstream removed the API. Refuse to write a
			// binding that silently binds nothing.
			if (functionCount == 0)
			{
				Console.Error.WriteLine("The parse produced zero functions. TRACY_ENABLE did not reach the header.");
				return 1;
			}

			Console.WriteLine($"Bindings written to {outputPath} ({functionCount} functions)");
			return 0;
		}

		/// <summary>
		/// Walks up from the build output until the sibling binding project is found, instead of
		/// hard-coding a fixed number of parent hops, which breaks whenever the RuntimeIdentifier
		/// or the publish layout changes the output depth.
		/// </summary>
		private static string ResolveOutputPath()
		{
			var current = new DirectoryInfo(AppContext.BaseDirectory);

			while (current != null)
			{
				var candidate = Path.Combine(current.FullName, "Evergine.Bindings.Tracy");
				if (Directory.Exists(candidate))
				{
					return Path.Combine(candidate, "Generated");
				}

				current = current.Parent;
			}

			return null;
		}
	}
}
