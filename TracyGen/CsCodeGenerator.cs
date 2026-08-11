using CppAst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TracyGen
{
	public class CsCodeGenerator
	{
		private const string Namespace = "Evergine.Bindings.Tracy";
		private const string NativeClass = "Tracy";
		private const string LibraryName = "TracyClient";

		public static readonly CsCodeGenerator Instance = new CsCodeGenerator();

		/// <summary>
		/// InlineArray wrapper types emitted for fixed-size arrays of non-primitive elements,
		/// keyed by generated type name.
		/// </summary>
		private readonly SortedDictionary<string, string> inlineArrayTypes = new SortedDictionary<string, string>();

		private CsCodeGenerator()
		{
		}

		public int Generate(CppCompilation compilation, string outputPath)
		{
			Helpers.TypedefList = compilation.Typedefs
				.Where(t => t.TypeKind == CppTypeKind.Typedef
					&& t.ElementType is CppPointerType pointer
					&& pointer.ElementType.TypeKind != CppTypeKind.Function)
				.Select(t => t.Name)
				.ToList();

			// Tracy declares `typedef struct ___tracy_c_zone_context TracyCZoneCtx;` — unlike
			// MuJoCo, the typedef and the struct differ by more than a trailing underscore, and
			// the typedef is the name the upstream documentation uses. Emit the struct under the
			// typedef's name so call sites and declaration agree.
			foreach (var typedef in compilation.Typedefs.Where(IsTracyElement))
			{
				if (Helpers.UnwrapTypedef(typedef.ElementType) is CppClass aliased
					&& !string.IsNullOrEmpty(aliased.Name)
					&& Helpers.NormalizeTypeName(aliased.Name) != typedef.Name)
				{
					Helpers.RegisterAnonymousName(aliased, typedef.Name);
				}
			}

			GenerateConstants(compilation, outputPath);
			GenerateEnums(compilation, outputPath);
			GenerateDelegates(compilation, outputPath);
			GenerateStructs(compilation, outputPath);
			return GenerateFunctions(compilation, outputPath);
		}

		/// <summary>
		/// True when the element comes from the vendored TracyC.h itself (not common/ nor the libc stubs). Without this filter
		/// everything reachable from the libc stubs and common/ would be emitted too, since ParseMacros
		/// pulls in the whole preprocessor state.
		/// </summary>
		private static bool IsTracyElement(CppElement element)
		{
			var file = element.Span.Start.File;
			if (string.IsNullOrEmpty(file))
			{
				return false;
			}

			return file.Replace('\\', '/').Contains("/Headers/tracy/", StringComparison.OrdinalIgnoreCase);
		}

		private static StreamWriter CreateFile(string outputPath, string fileName, params string[] usings)
		{
			var file = new StreamWriter(Path.Combine(outputPath, fileName));

			foreach (var @using in usings)
			{
				file.WriteLine($"using {@using};");
			}

			file.WriteLine();
			file.WriteLine($"namespace {Namespace}");
			file.WriteLine("{");

			return file;
		}

		private static void CloseFile(StreamWriter file)
		{
			file.WriteLine("}");
			file.Dispose();
		}

		// -------------------------------------------------------------------------------------
		// Constants
		// -------------------------------------------------------------------------------------

		private void GenerateConstants(CppCompilation compilation, string outputPath)
		{
			using var file = CreateFile(outputPath, "Constants.cs", "System");

			file.WriteLine($"\tpublic static partial class {NativeClass}");
			file.WriteLine("\t{");

			var emitted = new HashSet<string>();

			foreach (var macro in compilation.Macros)
			{
				if (!IsTracyElement(macro) || macro.Parameters != null)
				{
					// Function-like macros (mjENABLED, mjMAX, mjMARKSTACK) have no C# equivalent.
					continue;
				}

				if (!Helpers.TryParseMacroValue(macro.Value, out var csValue, out var csType))
				{
					// Alias macros such as "#define mju_sqrt sqrt" land here and are dropped.
					continue;
				}

				var name = Helpers.EscapeReservedKeyword(Helpers.StripPrefix(macro.Name));
				if (!emitted.Add(name))
				{
					continue;
				}

				file.WriteLine($"\t\tpublic const {csType} {name} = {csValue};");
			}

			file.WriteLine("\t}");
			CloseFile(file);
		}

		// -------------------------------------------------------------------------------------
		// Enums
		// -------------------------------------------------------------------------------------

		private void GenerateEnums(CppCompilation compilation, string outputPath)
		{
			using var file = CreateFile(outputPath, "Enums.cs", "System");

			var enums = compilation.Enums
				.Where(e => IsTracyElement(e) && !e.IsAnonymous && e.Items.Count > 0)
				.ToList();

			bool first = true;
			foreach (var @enum in enums)
			{
				if (!first)
				{
					file.WriteLine();
				}

				first = false;

				var enumName = Helpers.NormalizeTypeName(@enum.Name);

				Helpers.PrintComments(file, @enum.Comment, "\t");

				if (compilation.Typedefs.Any(t => t.Name == enumName + "Flags"))
				{
					file.WriteLine("\t[Flags]");
				}

				file.WriteLine($"\tpublic enum {enumName}");
				file.WriteLine("\t{");

				foreach (var item in @enum.Items)
				{
					var itemName = Helpers.EscapeReservedKeyword(Helpers.ScreamingToPascalCase(Helpers.StripPrefix(item.Name)));
					file.WriteLine($"\t\t{itemName} = {item.Value},");
				}

				file.WriteLine("\t}");
			}

			CloseFile(file);
		}

		// -------------------------------------------------------------------------------------
		// Delegates
		// -------------------------------------------------------------------------------------

		private void GenerateDelegates(CppCompilation compilation, string outputPath)
		{
			using var file = CreateFile(outputPath, "Delegates.cs", "System", "System.Runtime.InteropServices");

			var delegates = compilation.Typedefs
				.Where(t => IsTracyElement(t) && Helpers.ResolvesToFunctionPointer(t, out _))
				.ToList();

			bool first = true;
			foreach (var typedef in delegates)
			{
				Helpers.ResolvesToFunctionPointer(typedef, out var functionType);

				if (!first)
				{
					file.WriteLine();
				}

				first = false;

				Helpers.PrintComments(file, typedef.Comment, "\t");

				var returnType = Helpers.ShowAsMarshalType(Helpers.ConvertToCSharpType(functionType.ReturnType), Helpers.Family.ret);
				var name = Helpers.NormalizeTypeName(typedef.Name);

				file.WriteLine("\t[UnmanagedFunctionPointer(CallingConvention.Cdecl)]");
				file.WriteLine($"\tpublic unsafe delegate {returnType} {name}({BuildParameterList(functionType.Parameters)});");
			}

			CloseFile(file);
		}

		private static string BuildParameterList(IEnumerable<CppParameter> parameters)
		{
			var parts = new List<string>();
			int index = 0;

			foreach (var parameter in parameters)
			{
				var type = GetParameterType(parameter.Type);
				var name = string.IsNullOrEmpty(parameter.Name) ? $"arg{index}" : parameter.Name;
				parts.Add($"{type} {Helpers.EscapeReservedKeyword(name)}");
				index++;
			}

			return string.Join(", ", parts);
		}

		/// <summary>
		/// Every const char* stays a raw byte*, never [MarshalAs(LPUTF8Str)] string. The string
		/// marshaller allocates a TEMPORARY buffer and frees it when the call returns, but Tracy
		/// retains several of these pointers (frame names, plot names, the strings inside a source
		/// location) and dereferences them later from the profiler thread — a marshalled string
		/// there is a use-after-free that corrupts captures instead of crashing. Which parameters
		/// Tracy copies and which it keeps is knowledge that belongs in the hand-written Profiler
		/// layer, so the raw layer takes pointers everywhere and stays honest.
		/// </summary>
		private static string GetParameterType(CppType type)
		{
			return Helpers.ShowAsMarshalType(Helpers.ConvertToCSharpType(type), Helpers.Family.param);
		}

		// -------------------------------------------------------------------------------------
		// Structs
		// -------------------------------------------------------------------------------------

		private void GenerateStructs(CppCompilation compilation, string outputPath)
		{
			this.inlineArrayTypes.Clear();

			var classes = compilation.Classes
				.Where(c => IsTracyElement(c)
					&& c.IsDefinition
					&& !c.IsAnonymous
					&& !string.IsNullOrEmpty(c.Name)
					&& c.ClassKind != CppClassKind.Class)
				.ToList();

			// Anonymous aggregates (mjVisual's six sub-structs, mjuiItem's union) get a synthesized
			// name before anything is written, so that fields can refer to them.
			foreach (var @class in classes)
			{
				RegisterAnonymousMembers(@class);
			}

			var body = new StringWriter();
			foreach (var @class in classes)
			{
				WriteStruct(body, @class);
			}

			// Opaque handles: TracyC.h forward-declares `struct __tracy_lockable_context_data`
			// and never defines it — the lock API only ever passes a pointer. CppAst does not
			// surface pure forward declarations in compilation.Classes, so they are collected
			// from the function signatures that reference them. An empty struct gives those
			// pointers a real type instead of collapsing them to void*.
			var definedNames = classes.Select(Helpers.GetGeneratedClassName).ToHashSet();
			var opaque = compilation.Functions
				.Where(IsTracyElement)
				.SelectMany(f => f.Parameters.Select(p => p.Type).Append(f.ReturnType))
				.Select(UnwrapToClass)
				.Where(c => c != null && !c.IsDefinition && !string.IsNullOrEmpty(c.Name))
				.Select(Helpers.GetGeneratedClassName)
				.Where(n => !definedNames.Contains(n))
				.Distinct()
				.ToList();

			foreach (var name in opaque)
			{
				body.WriteLine();
				body.WriteLine("\t/// <summary>Opaque — upstream never defines this type; it is only handled by pointer.</summary>");
				body.WriteLine($"\tpublic struct {name}");
				body.WriteLine("\t{");
				body.WriteLine("\t}");
			}

			using var file = CreateFile(outputPath, "Structs.cs", "System", "System.Runtime.CompilerServices", "System.Runtime.InteropServices");

			foreach (var inlineArray in this.inlineArrayTypes.Values)
			{
				file.Write(inlineArray);
			}

			file.Write(body.ToString());
			CloseFile(file);
		}

		private static void RegisterAnonymousMembers(CppClass @class)
		{
			var parentName = Helpers.GetGeneratedClassName(@class);
			int anonymousIndex = 0;

			foreach (var field in @class.Fields)
			{
				if (GetAnonymousClass(field.Type) is not CppClass anonymous)
				{
					continue;
				}

				var suffix = string.IsNullOrEmpty(field.Name) ? $"anonymous{anonymousIndex++}" : field.Name;
				Helpers.RegisterAnonymousName(anonymous, $"{parentName}_{suffix}");
				RegisterAnonymousMembers(anonymous);
			}

			// An anonymous union declared without a member name (mjuiItem) is reported as a nested
			// class with no corresponding field; it still needs a type and a field of its own.
			foreach (var nested in @class.Classes.Where(c => c.IsAnonymous && !Helpers.HasAnonymousName(c)))
			{
				Helpers.RegisterAnonymousName(nested, $"{parentName}_anonymous{anonymousIndex++}");
				RegisterAnonymousMembers(nested);
			}
		}

		/// <summary>
		/// Peels pointers, qualifiers and typedefs down to a class, or null when the chain ends
		/// anywhere else. Used to find opaque handle types referenced from function signatures.
		/// </summary>
		private static CppClass UnwrapToClass(CppType type)
		{
			while (true)
			{
				switch (type)
				{
					case CppPointerType pointer:
						type = pointer.ElementType;
						continue;
					case CppQualifiedType qualified:
						type = qualified.ElementType;
						continue;
					case CppTypedef typedef:
						type = typedef.ElementType;
						continue;
					case CppClass @class:
						return @class;
					default:
						return null;
				}
			}
		}

		private static CppClass GetAnonymousClass(CppType type)
		{
			if (type is CppQualifiedType qualified)
			{
				type = qualified.ElementType;
			}

			return type is CppClass @class && @class.IsAnonymous ? @class : null;
		}

		private void WriteStruct(TextWriter file, CppClass @class)
		{
			// Nested anonymous aggregates are emitted as top-level types first.
			foreach (var field in @class.Fields)
			{
				if (GetAnonymousClass(field.Type) is CppClass anonymous)
				{
					WriteStruct(file, anonymous);
				}
			}

			foreach (var nested in @class.Classes.Where(c => c.IsAnonymous && !HasBackingField(@class, c)))
			{
				WriteStruct(file, nested);
			}

			var name = Helpers.GetGeneratedClassName(@class);
			bool isUnion = @class.ClassKind == CppClassKind.Union;

			file.WriteLine();
			Helpers.PrintComments(file, @class.Comment, "\t");
			file.WriteLine($"\t[StructLayout(LayoutKind.{(isUnion ? "Explicit" : "Sequential")})]");
			file.WriteLine($"\tpublic unsafe struct {name}");
			file.WriteLine("\t{");

			foreach (var field in @class.Fields)
			{
				WriteField(file, field, isUnion);
			}

			foreach (var nested in @class.Classes.Where(c => c.IsAnonymous && !HasBackingField(@class, c)))
			{
				var nestedName = Helpers.GetGeneratedClassName(nested);
				if (isUnion)
				{
					file.WriteLine("\t\t[FieldOffset(0)]");
				}

				file.WriteLine($"\t\tpublic {nestedName} {nestedName.Substring(nestedName.LastIndexOf('_') + 1)};");
			}

			file.WriteLine("\t}");
		}

		private static bool HasBackingField(CppClass parent, CppClass nested)
		{
			return parent.Fields.Any(f => ReferenceEquals(GetAnonymousClass(f.Type), nested));
		}

		private void WriteField(TextWriter file, CppField field, bool isUnion)
		{
			var rawName = field.Name;
			if (string.IsNullOrEmpty(rawName) && GetAnonymousClass(field.Type) is CppClass unnamed)
			{
				// An anonymous union declared without a member name (mjuiItem) is exposed under the
				// suffix of its synthesized type, so the C# name stays predictable.
				var typeName = Helpers.GetGeneratedClassName(unnamed);
				rawName = typeName.Substring(typeName.LastIndexOf('_') + 1);
			}

			var fieldName = Helpers.EscapeReservedKeyword(Helpers.PascalCaseField(rawName));

			Helpers.PrintComments(file, field.Comment, "\t\t");

			if (isUnion)
			{
				file.WriteLine("\t\t[FieldOffset(0)]");
			}

			var type = field.Type;
			if (type is CppQualifiedType qualified)
			{
				type = qualified.ElementType;
			}

			if (type is CppArrayType arrayType)
			{
				var length = Helpers.GetFlattenedArrayLength(arrayType, out var elementType);
				var elementCsType = Helpers.ShowAsMarshalType(Helpers.ConvertToCSharpType(elementType), Helpers.Family.field);

				if (Helpers.IsFixedBufferElement(elementCsType))
				{
					file.WriteLine($"\t\tpublic fixed {elementCsType} {fieldName}[{length}];");
				}
				else
				{
					// fixed buffers only accept primitives, so arrays of structs use an InlineArray.
					var wrapper = this.GetOrCreateInlineArrayType(elementCsType, length);
					file.WriteLine($"\t\tpublic {wrapper} {fieldName};");
				}

				return;
			}

			var csType = Helpers.ShowAsMarshalType(Helpers.ConvertToCSharpType(type), Helpers.Family.field);
			file.WriteLine($"\t\tpublic {csType} {fieldName};");
		}

		private string GetOrCreateInlineArrayType(string elementCsType, int length)
		{
			var name = $"InlineArray_{elementCsType.Replace("*", "Ptr")}_{length}";

			if (!this.inlineArrayTypes.ContainsKey(name))
			{
				var writer = new StringWriter();
				writer.WriteLine();
				writer.WriteLine($"\t[InlineArray({length})]");
				writer.WriteLine($"\tpublic unsafe struct {name}");
				writer.WriteLine("\t{");
				writer.WriteLine($"\t\tprivate {elementCsType} element0;");
				writer.WriteLine("\t}");

				this.inlineArrayTypes[name] = writer.ToString();
			}

			return name;
		}

		// -------------------------------------------------------------------------------------
		// Functions
		// -------------------------------------------------------------------------------------

		private int GenerateFunctions(CppCompilation compilation, string outputPath)
		{
			using var file = CreateFile(outputPath, "Functions.cs", "System", "System.Runtime.InteropServices");

			file.WriteLine($"\tpublic static unsafe partial class {NativeClass}");
			file.WriteLine("\t{");

			var functions = compilation.Functions
				.Where(f => IsTracyElement(f)
					&& !f.Flags.HasFlag(CppFunctionFlags.Inline)
					&& !f.Flags.HasFlag(CppFunctionFlags.FunctionTemplate))
				.ToList();

			var emitted = new HashSet<string>();

			bool first = true;
			foreach (var function in functions)
			{
				var csName = Helpers.EscapeReservedKeyword(Helpers.StripPrefix(function.Name));
				if (!emitted.Add(function.Name))
				{
					continue;
				}

				if (!first)
				{
					file.WriteLine();
				}

				first = false;

				Helpers.PrintComments(file, function.Comment, "\t\t");

				var returnType = Helpers.ShowAsMarshalType(Helpers.ConvertToCSharpType(function.ReturnType), Helpers.Family.ret);

				file.WriteLine($"\t\t[DllImport(\"{LibraryName}\", EntryPoint = \"{function.Name}\", CallingConvention = CallingConvention.Cdecl)]");
				file.WriteLine($"\t\tpublic static extern {returnType} {csName}({BuildParameterList(function.Parameters)});");
			}

			file.WriteLine("\t}");
			CloseFile(file);
			return emitted.Count;
		}
	}
}
