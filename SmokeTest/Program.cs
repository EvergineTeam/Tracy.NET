// Exercises the CPU surface of the package against the real native library. Reaching the
// end at all means TracyClient was found in runtimes/<rid>/native/ and every P/Invoke used
// here resolved. The client is built *without* TRACY_ON_DEMAND (see binding.yml for the
// measurement behind that), so it records from process start and buffers in memory; with no
// viewer connected nothing touches the network and the run is deterministic in CI.
//
// What this cannot verify: that a viewer receives sensible data — and, for the GPU section
// below, anything at all about collecting real timestamps, since the clock here is synthetic.
// Dx12FormsSample covers both against an actual DirectX 12 device.

using System;
using Evergine.Bindings.Tracy;

// Diagnostic harness: TRACY_SMOKE_LOOP=1 keeps the process alive emitting forever so a
// capture server can connect; TRACY_SMOKE_GPU=0 disables the GPU section to bisect which
// half of the emission crashes a server, if one does.
bool loop = Environment.GetEnvironmentVariable("TRACY_SMOKE_LOOP") == "1";
bool withGpu = Environment.GetEnvironmentVariable("TRACY_SMOKE_GPU") != "0";

if (loop)
{
	Profiler.SetThreadName("smoke-loop");
	// TRACY_SMOKE_BASE: starting clock value, to reproduce the raw magnitudes a real GPU
	// returns. TRACY_SMOKE_SYNC: call TimeSync every N frames (0 = never).
	long clock = long.TryParse(Environment.GetEnvironmentVariable("TRACY_SMOKE_BASE"), out var bb) ? bb : 0;
	int syncEvery = int.TryParse(Environment.GetEnvironmentVariable("TRACY_SMOKE_SYNC"), out var ss) ? ss : 0;
	float period = float.TryParse(Environment.GetEnvironmentVariable("TRACY_SMOKE_PERIOD"), out var pp) ? pp : 1.0f;

	GpuProfilerContext loopGpu = withGpu
		? GpuProfilerContext.Create("smoke-gpu", TracyGpuContextType.Custom, clock, period, 64)
		: null;
	Console.WriteLine($"looping forever, gpu={withGpu} base={clock} sync={syncEvery} period={period}");
	int i = 0;
	while (true)
	{
		using (var z = Profiler.BeginZone("loop-frame"))
		{
			System.Threading.Thread.Sleep(5);
		}

		if (loopGpu != null)
		{
			var gz = loopGpu.BeginZone("loop-gpu");
			long b = clock += 1_000, e = clock += 500;
			gz.End();
			loopGpu.SubmitTime(gz.BeginQueryId, b);
			loopGpu.SubmitTime(gz.EndQueryId, e);

			if (syncEvery > 0 && ++i % syncEvery == 0)
			{
				loopGpu.TimeSync(clock);
			}
		}

		Profiler.FrameMark();
	}
}

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
