using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Evergine.Bindings.Tracy
{

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_source_location_data
	{
		public byte* name;
		public byte* function;
		public byte* file;
		public uint line;
		public uint color;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct TracyCZoneCtx
	{
		public uint id;
		public int active;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_time_data
	{
		public long gpuTime;
		public ushort queryId;
		public byte context;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_zone_begin_data
	{
		public ulong srcloc;
		public ushort queryId;
		public byte context;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_zone_begin_callstack_data
	{
		public ulong srcloc;
		public int depth;
		public ushort queryId;
		public byte context;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_zone_end_data
	{
		public ushort queryId;
		public byte context;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_new_context_data
	{
		public long gpuTime;
		public float period;
		public byte context;
		public byte flags;
		public byte type;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_context_name_data
	{
		public byte context;
		public byte* name;
		public ushort len;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_calibration_data
	{
		public long gpuTime;
		public long cpuDelta;
		public byte context;
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct ___tracy_gpu_time_sync_data
	{
		public long gpuTime;
		public byte context;
	}

	/// <summary>Opaque — upstream never defines this type; it is only handled by pointer.</summary>
	public struct __tracy_lockable_context_data
	{
	}

	/// <summary>Opaque — upstream never defines this type; it is only handled by pointer.</summary>
	public struct __tracy_shared_lockable_context_data
	{
	}
}
