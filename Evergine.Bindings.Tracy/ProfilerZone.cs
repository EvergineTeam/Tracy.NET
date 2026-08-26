using System;
using System.Text;

namespace Evergine.Bindings.Tracy
{
	/// <summary>
	/// An open profiling zone. A ref struct so it cannot escape to the heap or outlive the
	/// stack frame it measures; dispose it (or let <c>using</c> do it) to close the zone.
	/// </summary>
	public readonly unsafe ref struct ProfilerZone
	{
		private readonly TracyCZoneCtx context;

		internal ProfilerZone(TracyCZoneCtx context)
		{
			this.context = context;
		}

		/// <summary>
		/// Renames this one zone instance. The name passed to <see cref="Profiler.BeginZone"/>
		/// belongs to the source location and is shared by every hit of that call site; this
		/// one is per instance and is what makes a zone say what it actually did — the cube
		/// count it drew, the asset it loaded. Tracy copies the string.
		///
		/// Only valid while this zone is the innermost open one on the thread: Tracy's zone
		/// events form a stack, and a name emitted after a nested zone opened would land on
		/// the nested zone instead.
		/// </summary>
		public void Name(string name)
		{
			var bytes = Encoding.UTF8.GetBytes(name);
			fixed (byte* ptr = bytes)
			{
				Tracy.___tracy_emit_zone_name(this.context, ptr, (nuint)bytes.Length);
			}
		}

		/// <summary>
		/// Recolors this one zone instance, overriding the color its source location was
		/// created with. Useful for the color to carry a measurement — a zone that turns red
		/// once it blows its budget stands out in the timeline without reading a single
		/// number.
		///
		/// Same stack rule as <see cref="Name"/>: emit it while this zone is the innermost
		/// open one. <see cref="TracyColor.None"/> is not a reset — it means "no color", and
		/// the viewer falls back to the color it derives from the source location.
		/// </summary>
		public void Color(TracyColor color)
		{
			Tracy.___tracy_emit_zone_color(this.context, (uint)color);
		}

		/// <summary>Attaches text to this zone in the capture. Tracy copies it.</summary>
		public void Text(string text)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			fixed (byte* ptr = bytes)
			{
				Tracy.___tracy_emit_zone_text(this.context, ptr, (nuint)bytes.Length);
			}
		}

		/// <summary>Attaches a numeric value to this zone.</summary>
		public void Value(ulong value)
		{
			Tracy.___tracy_emit_zone_value(this.context, value);
		}

		public void Dispose()
		{
			Tracy.___tracy_emit_zone_end(this.context);
		}
	}
}
