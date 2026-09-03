// Exercises the CPU surface of the package against the real native library. Reaching the
// end at all means TracyClient was found in runtimes/<rid>/native/ and every P/Invoke used
// here resolved. The client is built *without* TRACY_ON_DEMAND (see binding.yml for the
// measurement behind that), so it records from process start and buffers in memory; with no
// viewer connected nothing touches the network and the run is deterministic in CI.
//
// What this cannot verify: that a viewer receives sensible data — and, for the GPU section
// below, anything at all about collecting real timestamps, since the clock here is synthetic.
// LowLevelFormsSample covers both against an actual DirectX 12 device.

using System;
using System.Runtime.InteropServices;
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

Profiler.PlotConfig("frame-index", TracyPlotFormatEnum.TracyPlotFormatNumber, color: TracyColor.SteelBlue);
Profiler.PlotConfig("scratch bytes", TracyPlotFormatEnum.TracyPlotFormatMemory, color: TracyColor.MediumPurple);

for (int frame = 0; frame < 100; frame++)
{
	using (var zone = Profiler.BeginZone("update", TracyColor.MediumSeaGreen))
	{
		zone.Text($"frame {frame}");
		zone.Value((ulong)frame);

		// Per-instance name and color: the only place in the repository that resolves
		// ___tracy_emit_zone_name and ___tracy_emit_zone_color in every shipped native.
		// They go here, before the nested zone below opens, because Tracy's zone events
		// form a stack and these only reach the innermost open zone.
		zone.Name($"update {frame}");
		zone.Color(frame % 10 == 0 ? TracyColor.Crimson : TracyColor.MediumSeaGreen);

		// A nested zone with an anonymous (call-site) name, hitting the srcloc cache path.
		using (Profiler.BeginZone())
		{
			System.Threading.Thread.SpinWait(1000);
		}
	}

	// The memory surface, against real native allocations. Balance is the whole point of
	// exercising it here: Tracy terminates a session on an unmatched free, so a capture that
	// survives this loop is also evidence that the wrapper pairs its events correctly.
	int scratchSize = 1024 + (frame * 16);
	IntPtr scratch = Marshal.AllocHGlobal(scratchSize);
	Profiler.MemAlloc(scratch, (nuint)scratchSize);
	Profiler.Plot("scratch bytes", scratchSize);
	Profiler.MemFree(scratch);
	Marshal.FreeHGlobal(scratch);

	// A named pool, and an arena released in one shot: allocation ids here are synthetic, which
	// Tracy allows — it is how GPU and defragmenting allocators get tracked at all.
	Profiler.MemAlloc((IntPtr)(0x1000 + frame), 256, "smoke-pool");
	Profiler.MemFree((IntPtr)(0x1000 + frame), "smoke-pool");

	Profiler.MemAlloc((IntPtr)0x2000, 4096, "smoke-arena");
	Profiler.MemAlloc((IntPtr)0x3000, 4096, "smoke-arena");
	Profiler.MemDiscard("smoke-arena");

	Profiler.Plot("frame-index", frame);
	Profiler.Message($"frame {frame} done", TracyMessageSeverity.TracyMessageSeverityInfo, TracyColor.SteelBlue);
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
	var zone = gpu.BeginZone("synthetic pass", TracyColor.DarkTurquoise);
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

// The calibrated flavour of the same protocol: the context is anchored on a GPU/CPU pair
// sampled "at the same instant" — here the fake clock against Stopwatch — and re-anchored
// with Calibrate, whose CPU delta comes from real Stopwatch ticks scaled to nanoseconds. The
// first zone pair has to be (0,1): pairs aligned to even ids never straddle the ring wrap.
long calibratedClock = 1_000_000;
long previousCpuTicks = System.Diagnostics.Stopwatch.GetTimestamp();
var calibrated = GpuProfilerContext.Create("smoke-gpu-calibrated", TracyGpuContextType.Custom,
	initialGpuTimestamp: calibratedClock, periodNs: 1.0f, queryCapacity: 64, calibrated: true);

var firstZone = calibrated.BeginZone("first pair");
if (firstZone.BeginQueryId != 0 || firstZone.EndQueryId != 1)
{
	Console.Error.WriteLine($"first zone pair is ({firstZone.BeginQueryId},{firstZone.EndQueryId}), expected (0,1)");
	return 1;
}

firstZone.End();
calibrated.SubmitTime(firstZone.BeginQueryId, calibratedClock += 1_000);
calibrated.SubmitTime(firstZone.EndQueryId, calibratedClock += 500);

for (int frame = 0; frame < 20; frame++)
{
	var zone = calibrated.BeginZone("calibrated pass", TracyColor.MediumSeaGreen);
	long begin = calibratedClock += 1_000;
	long end = calibratedClock += 500;
	zone.End();
	calibrated.SubmitTime(zone.BeginQueryId, begin);
	calibrated.SubmitTime(zone.EndQueryId, end);

	if (frame % 5 == 4)
	{
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		long cpuDeltaNs = (long)((now - previousCpuTicks) * (1e9 / System.Diagnostics.Stopwatch.Frequency));
		previousCpuTicks = now;
		calibrated.Calibrate(calibratedClock, cpuDeltaNs);
	}
}

// TimeSync is the uncalibrated re-anchor and Calibrate the calibrated one; each is refused or
// ignored on the other kind of context so a consumer cannot mix the two schemes by accident.
calibrated.TimeSync(calibratedClock);
try
{
	gpu.Calibrate(fakeClock, 1_000_000);
	Console.Error.WriteLine("Calibrate on an uncalibrated context did not throw");
	return 1;
}
catch (InvalidOperationException)
{
}

Console.WriteLine("21 calibrated GPU zones and 4 calibrations emitted. Exit 0.");
Console.WriteLine("100 frames of zones, plots, messages and frame marks emitted. Exit 0.");
return 0;
