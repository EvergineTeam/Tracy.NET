// Exercises the CPU surface of the package against the real native library. Reaching the
// end at all means TracyClient was found in runtimes/<rid>/native/ and every P/Invoke used
// here resolved. The client is built with TRACY_ON_DEMAND, so without a viewer connected
// nothing touches the network and the run is deterministic in CI.
//
// What this cannot verify: that a viewer receives sensible data. That is the human check in
// the release process — connect Tracy v0.14 to this same binary and watch zones arrive.

using System;
using Evergine.Bindings.Tracy;

Profiler.SetThreadName("smoke-main");
Profiler.AppInfo("Evergine.Bindings.Tracy package smoke test");

Console.WriteLine($"Tracy client loaded. Connected: {Profiler.IsConnected}");

for (int frame = 0; frame < 100; frame++)
{
	using (var zone = Profiler.BeginZone("update"))
	{
		zone.Text($"frame {frame}");
		zone.Value((ulong)frame);

		// A nested zone with an anonymous (call-site) name, hitting the srcloc cache path.
		using (Profiler.BeginZone())
		{
			System.Threading.Thread.SpinWait(1000);
		}
	}

	Profiler.Plot("frame-index", frame);
	Profiler.Message($"frame {frame} done");
	Profiler.FrameMark("aux");
	Profiler.FrameMark();
}

Console.WriteLine("100 frames of zones, plots, messages and frame marks emitted. Exit 0.");
return 0;
