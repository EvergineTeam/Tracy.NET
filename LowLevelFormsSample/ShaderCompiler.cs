using System;
using Evergine.Common.Graphics;
using Vortice.Dxc;

namespace LowLevelFormsSample
{
	/// <summary>
	/// Compiles the sample's HLSL for whichever backend is running. DirectX 12 compiles through
	/// the graphics context; Evergine's Vulkan backend does not compile at all (it expects SPIR-V
	/// bytes, which Evergine Studio produces offline), so this class runs the same DXC the DirectX
	/// backend uses, with the <c>-spirv</c> target and the register shifts Evergine's Vulkan
	/// resource layouts assume (b at 0, u at 20, s at 40, t at 60, from the engine's own
	/// shaderTranslator.ps1 scripts). One HLSL source, two backends, no shader files on disk.
	/// </summary>
	internal static class ShaderCompiler
	{
		public static byte[] Compile(GraphicsContext graphics, string source, string entryPoint, ShaderStages stage)
		{
			if (graphics.BackendType == GraphicsBackend.Vulkan)
			{
				return CompileSpirv(source, entryPoint, stage);
			}

			CompilationResult result = graphics.ShaderCompile(source, entryPoint, stage);
			if (result.HasErrors || result.ByteCode == null)
			{
				throw new InvalidOperationException($"{graphics.BackendType} shader compilation of {entryPoint} failed: {result.Message}");
			}

			return result.ByteCode;
		}

		private static byte[] CompileSpirv(string source, string entryPoint, ShaderStages stage)
		{
			string profile = stage switch
			{
				ShaderStages.Vertex => "vs_5_0",
				ShaderStages.Pixel => "ps_5_0",
				_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "The sample only has vertex and pixel shaders."),
			};

			// -Zpr matches the row-major packing the DirectX 12 backend compiles with, so the matrices
			// the sample uploads mean the same thing on both APIs.
			string[] arguments =
			{
				"-E", entryPoint,
				"-T", profile,
				"-spirv",
				"-Zpr",
				"-fvk-u-shift", "20", "all",
				"-fvk-s-shift", "40", "all",
				"-fvk-t-shift", "60", "all",
			};

			using IDxcResult result = DxcCompiler.Compile(source, arguments, null);
			if (result.GetStatus().Failure)
			{
				throw new InvalidOperationException($"SPIR-V compilation of {entryPoint} failed: {result.GetErrors()}");
			}

			return result.GetObjectBytecodeArray();
		}
	}
}
