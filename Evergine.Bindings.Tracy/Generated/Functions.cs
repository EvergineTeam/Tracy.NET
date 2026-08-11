using System;
using System.Runtime.InteropServices;

namespace Evergine.Bindings.Tracy
{
	public static unsafe partial class Tracy
	{
		[DllImport("TracyClient", EntryPoint = "___tracy_set_thread_name", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_set_thread_name(byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_alloc_srcloc", CallingConvention = CallingConvention.Cdecl)]
		public static extern ulong ___tracy_alloc_srcloc(uint line, byte* source, nuint sourceSz, byte* function, nuint functionSz, uint color);

		[DllImport("TracyClient", EntryPoint = "___tracy_alloc_srcloc_name", CallingConvention = CallingConvention.Cdecl)]
		public static extern ulong ___tracy_alloc_srcloc_name(uint line, byte* source, nuint sourceSz, byte* function, nuint functionSz, byte* name, nuint nameSz, uint color);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_begin", CallingConvention = CallingConvention.Cdecl)]
		public static extern TracyCZoneCtx ___tracy_emit_zone_begin(___tracy_source_location_data* srcloc, int active);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_begin_callstack", CallingConvention = CallingConvention.Cdecl)]
		public static extern TracyCZoneCtx ___tracy_emit_zone_begin_callstack(___tracy_source_location_data* srcloc, int depth, int active);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_begin_alloc", CallingConvention = CallingConvention.Cdecl)]
		public static extern TracyCZoneCtx ___tracy_emit_zone_begin_alloc(ulong srcloc, int active);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_begin_alloc_callstack", CallingConvention = CallingConvention.Cdecl)]
		public static extern TracyCZoneCtx ___tracy_emit_zone_begin_alloc_callstack(ulong srcloc, int depth, int active);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_end", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_zone_end(TracyCZoneCtx ctx);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_text", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_zone_text(TracyCZoneCtx ctx, byte* txt, nuint size);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_text_fmt", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_zone_text_fmt(TracyCZoneCtx ctx, byte* fmt);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_name", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_zone_name(TracyCZoneCtx ctx, byte* txt, nuint size);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_name_fmt", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_zone_name_fmt(TracyCZoneCtx ctx, byte* fmt);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_color", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_zone_color(TracyCZoneCtx ctx, uint color);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_zone_value", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_zone_value(TracyCZoneCtx ctx, ulong value);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin(___tracy_gpu_zone_begin_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin_callstack", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin_callstack(___tracy_gpu_zone_begin_callstack_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin_alloc", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin_alloc(___tracy_gpu_zone_begin_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin_alloc_callstack", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin_alloc_callstack(___tracy_gpu_zone_begin_callstack_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_end", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_end(___tracy_gpu_zone_end_data data);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_time", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_time(___tracy_gpu_time_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_new_context", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_new_context(___tracy_gpu_new_context_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_context_name", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_context_name(___tracy_gpu_context_name_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_calibration", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_calibration(___tracy_gpu_calibration_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_time_sync", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_time_sync(___tracy_gpu_time_sync_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin_serial(___tracy_gpu_zone_begin_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin_callstack_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin_callstack_serial(___tracy_gpu_zone_begin_callstack_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin_alloc_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin_alloc_serial(___tracy_gpu_zone_begin_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_begin_alloc_callstack_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_begin_alloc_callstack_serial(___tracy_gpu_zone_begin_callstack_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_zone_end_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_zone_end_serial(___tracy_gpu_zone_end_data data);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_time_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_time_serial(___tracy_gpu_time_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_new_context_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_new_context_serial(___tracy_gpu_new_context_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_context_name_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_context_name_serial(___tracy_gpu_context_name_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_calibration_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_calibration_serial(___tracy_gpu_calibration_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_gpu_time_sync_serial", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_gpu_time_sync_serial(___tracy_gpu_time_sync_data arg0);

		[DllImport("TracyClient", EntryPoint = "___tracy_connected", CallingConvention = CallingConvention.Cdecl)]
		public static extern int ___tracy_connected();

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_alloc", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_alloc(void* ptr, nuint size);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_alloc_callstack", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_alloc_callstack(void* ptr, nuint size, int depth);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_free", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_free(void* ptr);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_free_callstack", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_free_callstack(void* ptr, int depth);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_alloc_named", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_alloc_named(void* ptr, nuint size, byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_alloc_callstack_named", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_alloc_callstack_named(void* ptr, nuint size, int depth, byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_free_named", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_free_named(void* ptr, byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_free_callstack_named", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_free_callstack_named(void* ptr, int depth, byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_discard", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_discard(byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_memory_discard_callstack", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_memory_discard_callstack(byte* name, int depth);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_logString", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_logString(sbyte severity, int color, int callstack_depth, nuint size, byte* txt);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_logStringL", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_logStringL(sbyte severity, int color, int callstack_depth, byte* txt);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_frame_mark", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_frame_mark(byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_frame_mark_start", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_frame_mark_start(byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_frame_mark_end", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_frame_mark_end(byte* name);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_frame_image", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_frame_image(void* image, ushort w, ushort h, byte offset, int flip);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_plot", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_plot(byte* name, double val);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_plot_float", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_plot_float(byte* name, float val);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_plot_int", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_plot_int(byte* name, long val);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_plot_config", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_plot_config(byte* name, int type, int step, int fill, uint color);

		[DllImport("TracyClient", EntryPoint = "___tracy_emit_message_appinfo", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_emit_message_appinfo(byte* txt, nuint size);

		[DllImport("TracyClient", EntryPoint = "___tracy_announce_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern __tracy_lockable_context_data* ___tracy_announce_lockable_ctx(___tracy_source_location_data* srcloc);

		[DllImport("TracyClient", EntryPoint = "___tracy_terminate_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_terminate_lockable_ctx(__tracy_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_before_lock_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern int ___tracy_before_lock_lockable_ctx(__tracy_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_lock_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_lock_lockable_ctx(__tracy_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_unlock_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_unlock_lockable_ctx(__tracy_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_try_lock_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_try_lock_lockable_ctx(__tracy_lockable_context_data* lockdata, int acquired);

		[DllImport("TracyClient", EntryPoint = "___tracy_mark_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_mark_lockable_ctx(__tracy_lockable_context_data* lockdata, ___tracy_source_location_data* srcloc);

		[DllImport("TracyClient", EntryPoint = "___tracy_custom_name_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_custom_name_lockable_ctx(__tracy_lockable_context_data* lockdata, byte* name, nuint nameSz);

		[DllImport("TracyClient", EntryPoint = "___tracy_announce_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern __tracy_shared_lockable_context_data* ___tracy_announce_shared_lockable_ctx(___tracy_source_location_data* srcloc);

		[DllImport("TracyClient", EntryPoint = "___tracy_terminate_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_terminate_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_before_lock_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern int ___tracy_before_lock_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_lock_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_lock_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_unlock_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_unlock_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_try_lock_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_try_lock_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata, int acquired);

		[DllImport("TracyClient", EntryPoint = "___tracy_before_lock_shared_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern int ___tracy_before_lock_shared_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_lock_shared_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_lock_shared_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_unlock_shared_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_unlock_shared_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata);

		[DllImport("TracyClient", EntryPoint = "___tracy_after_try_lock_shared_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_after_try_lock_shared_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata, int acquired);

		[DllImport("TracyClient", EntryPoint = "___tracy_mark_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_mark_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata, ___tracy_source_location_data* srcloc);

		[DllImport("TracyClient", EntryPoint = "___tracy_custom_name_shared_lockable_ctx", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_custom_name_shared_lockable_ctx(__tracy_shared_lockable_context_data* lockdata, byte* name, nuint nameSz);

		[DllImport("TracyClient", EntryPoint = "___tracy_begin_sampling_profiling", CallingConvention = CallingConvention.Cdecl)]
		public static extern int ___tracy_begin_sampling_profiling();

		[DllImport("TracyClient", EntryPoint = "___tracy_end_sampling_profiling", CallingConvention = CallingConvention.Cdecl)]
		public static extern void ___tracy_end_sampling_profiling();

		[DllImport("TracyClient", EntryPoint = "___tracy_get_time", CallingConvention = CallingConvention.Cdecl)]
		public static extern long ___tracy_get_time();
	}
}
