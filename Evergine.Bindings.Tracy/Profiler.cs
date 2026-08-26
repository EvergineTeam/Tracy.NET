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

		// There is deliberately no cache of ___tracy_source_location_data structs here. The
		// non-alloc zone path takes a pointer to one and treats it as compile-time static
		// data; pointers into managed-owned native memory never resolve on the server side
		// (zones arrive named "???" — observed, not theorized). Every zone therefore goes
		// through the alloc path, where the client copies the strings during the call: the
		// per-call-site cost is one ___tracy_alloc_srcloc_name, and the strings themselves
		// are interned once below.

		/// <summary>True while a Tracy viewer is connected to this application.</summary>
		public static bool IsConnected => Tracy.___tracy_connected() != 0;

		/// <summary>
		/// Opens a profiling zone at the call site. Dispose the returned value to close it —
		/// typically with <c>using var zone = Profiler.BeginZone();</c>. The source location is
		/// captured by the compiler, interned on first use, and reused on every later hit.
		///
		/// <paramref name="color"/> belongs to that source location, so it is the color every
		/// hit of this call site gets. To color one hit by what it measured, call
		/// <see cref="ProfilerZone.Color"/> on the returned zone instead.
		/// </summary>
		public static ProfilerZone BeginZone(
			string name = null,
			TracyColor color = TracyColor.None,
			[CallerLineNumber] int line = 0,
			[CallerFilePath] string file = "",
			[CallerMemberName] string member = "")
		{
			var srcloc = AllocSourceLocation(file, member, line, name, color);
			var ctx = Tracy.___tracy_emit_zone_begin_alloc(srcloc, 1);
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

		/// <summary>
		/// Configures how the viewer draws a plot. Call it once, before the first
		/// <see cref="Plot"/> of that series — the name goes through the same intern table, so
		/// the same string reaches Tracy as the same pointer and the configuration lands on the
		/// series it was meant for.
		///
		/// <paramref name="format"/> is what makes a plot readable: a byte count left at
		/// <see cref="TracyPlotFormatEnum.TracyPlotFormatNumber"/> is drawn as a bare figure,
		/// while <see cref="TracyPlotFormatEnum.TracyPlotFormatMemory"/> formats it as KB/MB.
		/// </summary>
		public static void PlotConfig(
			string name,
			TracyPlotFormatEnum format = TracyPlotFormatEnum.TracyPlotFormatNumber,
			bool step = false,
			bool fill = true,
			TracyColor color = TracyColor.None)
		{
			Tracy.___tracy_emit_plot_config((byte*)Intern(name), (int)format, step ? 1 : 0, fill ? 1 : 0, (uint)color);
		}

		/// <summary>Sends a log message to the capture. Tracy copies the text; nothing is retained.</summary>
		public static void Message(string text, TracyMessageSeverity severity = TracyMessageSeverity.TracyMessageSeverityInfo, TracyColor color = TracyColor.None)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			fixed (byte* ptr = bytes)
			{
				Tracy.___tracy_emit_logString((sbyte)severity, (int)color, 0, (nuint)bytes.Length, ptr);
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

		/// <summary>
		/// Reports an allocation, feeding the viewer's memory graph, allocation list and memory
		/// map. Pair it with exactly one <see cref="MemFree(IntPtr)"/>.
		///
		/// NOTE — this is the one part of the API that does not tolerate a mistake. Tracy
		/// *terminates the session* on a free without a matching allocation, on the same address
		/// allocated twice without a free in between, or on a double free; a capture that dies on
		/// its own is nearly always one of those three. The relief Tracy grants to on-demand
		/// clients does not apply here: these natives are built without TRACY_ON_DEMAND
		/// (see binding.yml), so the books must balance from the first event of the process.
		///
		/// The pointer does not have to be a real address. Tracy accepts unique numeric ids —
		/// which is how GPU or defragmenting allocators get tracked at all — at the cost of the
		/// memory map, which stops meaning anything.
		/// </summary>
		public static void MemAlloc(IntPtr ptr, nuint size)
		{
			Tracy.___tracy_emit_memory_alloc((void*)ptr, size);
		}

		/// <summary>Reports a deallocation. See <see cref="MemAlloc(IntPtr, nuint)"/> for the balance rule.</summary>
		public static void MemFree(IntPtr ptr)
		{
			Tracy.___tracy_emit_memory_free((void*)ptr);
		}

		/// <summary>
		/// Reports an allocation into a separate memory pool, which the viewer lists on its own in
		/// the memory window — the way to keep graphics-API memory, a scripting heap or an arena
		/// from being averaged into the general one.
		///
		/// The pool name is interned for the lifetime of the process, like frame and plot names:
		/// Tracy identifies a pool by the pointer, not by the characters, so the same name has to
		/// arrive as the same pointer every time.
		/// </summary>
		public static void MemAlloc(IntPtr ptr, nuint size, string pool)
		{
			Tracy.___tracy_emit_memory_alloc_named((void*)ptr, size, (byte*)Intern(pool));
		}

		/// <summary>Reports a deallocation from a named pool. Same balance rule, per pool.</summary>
		public static void MemFree(IntPtr ptr, string pool)
		{
			Tracy.___tracy_emit_memory_free_named((void*)ptr, (byte*)Intern(pool));
		}

		/// <summary>
		/// Releases every outstanding allocation of a pool at once. This is what makes arena and
		/// bump allocators reportable: they hand out an ever-advancing pointer and cannot free an
		/// individual object, only reset the whole thing.
		///
		/// After a discard the pool starts empty, so handing out the same addresses again is
		/// legal — without it, the second round would look like allocating a live address twice
		/// and would end the capture.
		/// </summary>
		public static void MemDiscard(string pool)
		{
			Tracy.___tracy_emit_memory_discard((byte*)Intern(pool));
		}

		// There is deliberately no wrapper over the _callstack memory variants. They feed the
		// viewer's allocation hot-spot tree, which Tracy builds from the *native* stack: from
		// .NET that is JIT-compiled frames with no symbols, so the tree would cost every
		// allocation a stack walk to show nothing anyone can act on.

		/// <summary>
		/// Builds a one-shot source location through the alloc path. The returned id is
		/// consumed by exactly one zone begin (the client frees it after use). Lengths are
		/// cached alongside the interned bytes so this stays two dictionary hits per call.
		/// </summary>
		internal static ulong AllocSourceLocation(string file, string member, int line, string name, TracyColor color)
		{
			var (filePtr, fileLen) = InternWithLength(file);
			var (memberPtr, memberLen) = InternWithLength(member);

			if (name == null)
			{
				return Tracy.___tracy_alloc_srcloc((uint)line, (byte*)filePtr, fileLen, (byte*)memberPtr, memberLen, (uint)color);
			}

			var (namePtr, nameLen) = InternWithLength(name);
			return Tracy.___tracy_alloc_srcloc_name((uint)line, (byte*)filePtr, fileLen, (byte*)memberPtr, memberLen, (byte*)namePtr, nameLen, (uint)color);
		}

		private static readonly ConcurrentDictionary<string, (IntPtr ptr, nuint len)> internedWithLength = new();

		private static (IntPtr ptr, nuint len) InternWithLength(string value)
		{
			return internedWithLength.GetOrAdd(value, static v =>
			{
				var bytes = Encoding.UTF8.GetBytes(v);
				var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
				bytes.CopyTo(new Span<byte>(ptr, bytes.Length));
				ptr[bytes.Length] = 0;
				return ((IntPtr)ptr, (nuint)bytes.Length);
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
