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

// The GPU layer, exercised with a synthetic clock — which is exactly what the agnostic
// design permits: no query heap, no driver, just the emission protocol. A Custom-type
// context with period 1.0 (one tick = one nanosecond) and a monotonically advancing
// fake timestamp per zone edge.
var gpu = GpuProfilerContext.Create("smoke-gpu", TracyGpuContextType.Custom,
	initialGpuTimestamp: 0, periodNs: 1.0f, queryCapacity: 64);

long fakeClock = 0;
for (int frame = 0; frame < 100; frame++)
{
	var zone = gpu.BeginZone("synthetic pass");
	long begin = fakeClock += 1_000;
	long end = fakeClock += 500;
	zone.End();

	gpu.SubmitTime(zone.BeginQueryId, begin);
	gpu.SubmitTime(zone.EndQueryId, end);

	if (frame % 50 == 0)
	{
		gpu.TimeSync(fakeClock);
	}
}

Console.WriteLine("100 synthetic GPU zones emitted through the ring. Exit 0.");
Console.WriteLine("100 frames of zones, plots, messages and frame marks emitted. Exit 0.");
return 0;
