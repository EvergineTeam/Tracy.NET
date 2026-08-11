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
