namespace Evergine.Bindings.Tracy
{
	/// <summary>
	/// Which graphics API a GPU context represents. Only affects how the viewer labels the
	/// track — the timestamp protocol is identical for all of them.
	///
	/// NOTE — hand-written, values pinned to tracy::GpuContextType in
	/// public/common/TracyQueue.hpp at v0.14.0. The enum lives in the C++ internals rather
	/// than TracyC.h, which is why the generator cannot produce it; if an upstream bump adds
	/// a value, this file is the one place to extend.
	/// </summary>
	public enum TracyGpuContextType : byte
	{
		Invalid = 0,
		OpenGl = 1,
		Vulkan = 2,
		OpenCL = 3,
		Direct3D12 = 4,
		Direct3D11 = 5,
		Metal = 6,
		Custom = 7,
		CUDA = 8,
		Rocprof = 9,
		WebGPU = 10,
	}
}
