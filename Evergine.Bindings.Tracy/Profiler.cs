using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Evergine.Bindings.Tracy
{
	/// <summary>
	/// Hand-written ergonomic layer over the generated raw binding. Tracy's C API is designed
	/// around compile-time macros that bake source locations into static storage; this class
	/// reproduces that contract at run time. The one rule everything below follows: strings
	/// Tracy *retains* (frame names, plot names, everything inside a source location) come from
	/// a process-lifetime intern table, and strings Tracy *copies* (messages, zone text, thread
	/// names) are passed as transient UTF-8. Mixing those two up corrupts captures instead of
	/// crashing, which is why the raw layer takes byte* everywhere and the knowledge lives here.
	/// </summary>
	public static unsafe class Profiler
	{
		/// <summary>
		/// UTF-8 copies of every string Tracy is allowed to keep a pointer to. Never freed:
		/// the profiler thread may read them at any point until process exit, exactly like the
		/// static storage the C macros would have used.
		/// </summary>
		private static readonly ConcurrentDictionary<string, IntPtr> internedStrings = new();

		/// <summary>
		/// Source locations by call site. The C macros create one static instance per source
		/// line; this table is the run-time equivalent, so a zone hit in a loop allocates once.
		/// </summary>
		private static readonly ConcurrentDictionary<(string file, string member, int line, string name, uint color), IntPtr> sourceLocations = new();

		/// <summary>True while a Tracy viewer is connected to this application.</summary>
		public static bool IsConnected => Tracy.___tracy_connected() != 0;

		/// <summary>
		/// Opens a profiling zone at the call site. Dispose the returned value to close it —
		/// typically with <c>using var zone = Profiler.BeginZone();</c>. The source location is
		/// captured by the compiler, interned on first use, and reused on every later hit.
		/// </summary>
		public static ProfilerZone BeginZone(
			string name = null,
			uint color = 0,
			[CallerLineNumber] int line = 0,
			[CallerFilePath] string file = "",
			[CallerMemberName] string member = "")
		{
			var srcloc = GetSourceLocation(file, member, line, name, color);
			var ctx = Tracy.___tracy_emit_zone_begin((___tracy_source_location_data*)srcloc, 1);
			return new ProfilerZone(ctx);
		}

		/// <summary>Marks the end of the main frame. Call once per frame from the render loop.</summary>
		public static void FrameMark()
		{
			Tracy.___tracy_emit_frame_mark(null);
		}

		/// <summary>
		/// Marks the end of a named auxiliary frame (a physics tick, an async loader pass).
		/// The name is interned: Tracy keeps the pointer for the lifetime of the process.
		/// </summary>
		public static void FrameMark(string name)
		{
			Tracy.___tracy_emit_frame_mark((byte*)Intern(name));
		}

		/// <summary>Plots a value in the named graph. The name is interned, the value is not.</summary>
		public static void Plot(string name, double value)
		{
			Tracy.___tracy_emit_plot((byte*)Intern(name), value);
		}

		/// <summary>Sends a log message to the capture. Tracy copies the text; nothing is retained.</summary>
		public static void Message(string text, TracyMessageSeverity severity = TracyMessageSeverity.TracyMessageSeverityInfo, int color = 0)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			fixed (byte* ptr = bytes)
			{
				Tracy.___tracy_emit_logString((sbyte)severity, color, 0, (nuint)bytes.Length, ptr);
			}
		}

		/// <summary>Names the current thread in the capture. Tracy copies the string.</summary>
		public static void SetThreadName(string name)
		{
			var bytes = Encoding.UTF8.GetBytes(name + "\0");
			fixed (byte* ptr = bytes)
			{
				Tracy.___tracy_set_thread_name(ptr);
			}
		}

		/// <summary>
		/// Attaches free-form build/version information to the capture header. Tracy copies it.
		/// </summary>
		public static void AppInfo(string text)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			fixed (byte* ptr = bytes)
			{
				Tracy.___tracy_emit_message_appinfo(ptr, (nuint)bytes.Length);
			}
		}

		private static IntPtr GetSourceLocation(string file, string member, int line, string name, uint color)
		{
			return sourceLocations.GetOrAdd((file, member, line, name, color), static key =>
			{
				var data = (___tracy_source_location_data*)NativeMemory.Alloc((nuint)sizeof(___tracy_source_location_data));
				data->name = key.name == null ? null : (byte*)Intern(key.name);
				data->function = (byte*)Intern(key.member);
				data->file = (byte*)Intern(key.file);
				data->line = (uint)key.line;
				data->color = key.color;
				return (IntPtr)data;
			});
		}

		private static IntPtr Intern(string value)
		{
			return internedStrings.GetOrAdd(value, static v =>
			{
				var bytes = Encoding.UTF8.GetBytes(v);
				var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
				bytes.CopyTo(new Span<byte>(ptr, bytes.Length));
				ptr[bytes.Length] = 0;
				return (IntPtr)ptr;
			});
		}
	}
}
