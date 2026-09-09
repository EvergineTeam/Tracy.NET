# Tracy.NET

This repository contains low-level bindings for the [Tracy profiler](https://github.com/wolfpld/tracy) CPU client used in [Evergine](https://evergine.com/).

[![CI](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CI.yml/badge.svg)](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CI.yml)
[![CD](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CD.yml/badge.svg)](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CD.yml)
[![Nuget](https://img.shields.io/nuget/v/Evergine.Bindings.Tracy?logo=nuget)](https://www.nuget.org/packages/Evergine.Bindings.Tracy)

## Purpose

Tracy is a real-time, nanosecond-resolution frame profiler. This package binds its C API
(`TracyC.h`) so a .NET application can be instrumented and inspected live from the
[Tracy viewer](https://github.com/wolfpld/tracy/releases): zones, frames, plots, messages
and thread names.

Two layers ship in one package:

- **`Tracy`** (generated): the raw 79 P/Invokes, byte-for-byte faithful to `TracyC.h`.
  Strings are `byte*` on purpose: Tracy retains several of those pointers and reads them
  later from the profiler thread, so no marshaller may free them behind its back.
- **`Profiler` / `ProfilerZone`** (hand-written): the API you actually use. Source
  locations are captured by the compiler (`[CallerFilePath]`/`[CallerLineNumber]`) and
  interned for the process lifetime, reproducing at run time what Tracy's C macros do at
  compile time.

```csharp
using Evergine.Bindings.Tracy;

Profiler.SetThreadName("main");

while (running)
{
    using (var zone = Profiler.BeginZone("update", TracyColor.MediumSeaGreen))
    {
        zone.Text(sceneName);
        Update();

        // Per instance, against the call-site name and color above: this is how a zone says
        // what it actually measured.
        zone.Name($"update {entityCount} entities");
        zone.Color(frameMs > budgetMs ? TracyColor.Crimson : TracyColor.MediumSeaGreen);
    }

    Profiler.Plot("entities", entityCount);
    Profiler.FrameMark();
}
```

### Memory

`Profiler.MemAlloc(ptr, size)` and `Profiler.MemFree(ptr)` feed the viewer's memory graph,
allocation list and memory map. Both take an `IntPtr`, so no `unsafe` is needed at the call
site, and the value does not have to be a real address, since Tracy accepts unique numeric ids,
which is how GPU or defragmenting allocators get tracked at all (the memory map is what you
give up).

Overloads taking a pool name track a separate pool, listed on its own in the memory window,
graphics-API memory apart from the general heap. `Profiler.MemDiscard(pool)` releases a whole
pool at once, which is the only kind of free an arena or bump allocator has.

```csharp
Profiler.MemAlloc(buffer.NativePointer, sizeInBytes, "gpu");
```

> **The books must balance.** Tracy *terminates the session* on a free without a matching
> allocation, on the same address allocated twice without a free in between, or on a double
> free. The relief Tracy grants to on-demand clients does not apply here: this package's
> natives are built without `TRACY_ON_DEMAND`, so the accounting must hold from the first event
> of the process. A capture that dies on its own is nearly always one of those three.

### Colors

`TracyColor` carries the palette from Tracy's own `TracyColor.hpp`, so a color is picked by
name rather than by remembering a hexadecimal. It is not a closed set: any RGB value works,
cast it: `(TracyColor)0x1a2b3c`.

Two things follow from how Tracy defines colors, both of which the API preserves rather than
papers over:

- `0` means **no color was set**, not black. A zone left at `TracyColor.None` keeps the color
  the viewer derives from its source location. Upstream declares `Black = 0x000000`, so
  `TracyColor.Black` is a synonym of `None`; for an actually black zone use
  `(TracyColor)0x000001`, which is what Tracy's manual recommends.
- The color and name passed to `BeginZone` belong to the **source location**, shared by every
  hit of that call site. `zone.Color()` and `zone.Name()` apply to **that one hit**, and are
  only valid while the zone is the innermost open one on the thread, since Tracy's zone events form
  a stack, so anything emitted after a nested zone opened lands on the nested zone instead.

## How the native client is built

The package carries `TracyClient` compiled from the same upstream tag the bindings are
generated from, with two defines that are part of the contract:

- `TRACY_ENABLE`: without it the API does not exist at all.
- `TRACY_ON_DEMAND` is **off**: the client records continuously from process start, so
  connecting the viewer at any point shows the full history. Measured on v0.14.0: the
  on-demand DLL build drops all data a few frames after a server connects, so continuous
  recording is not a preference here, it is the mode that works. The cost: an instrumented
  application accumulates events in memory until a viewer connects, so ship Tracy builds
  to the field deliberately, not by default.

## Supported Platforms

- [x] Windows x64
- [x] Windows ARM64
- [x] Linux x64
- [x] Linux ARM64
- [x] macOS (universal: Apple Silicon + Intel)

Browser WASM is excluded by design: the Tracy client opens a socket and spawns worker
threads, neither of which exists in the .NET browser-wasm sandbox.

How each identifier is checked before a release is published, stated rather than glossed:

| | how it is checked |
|---|---|
| `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64` | the package is installed from the real `.nupkg` and a hundred frames of zones, plots, messages and frame marks are emitted through the native library |
| `win-arm64` | **not executed**: no ARM64 runner in the matrix. The application is published for it and `TracyClient.dll` has to reach the output, so the evidence is the file, not a run |
| `osx-x64` | served by the same universal `osx` dylib as `osx-arm64`; the arm64 half is what gets executed in CI |

What no CI leg can verify: that a viewer receives sensible data. That is a human step in
the release process: connect Tracy v0.14 to the smoke test binary and watch zones arrive.

## GPU zones

The package also ships a graphics-API-agnostic GPU layer: `GpuProfilerContext` and
`GpuZone` over Tracy's `___tracy_emit_gpu_*_serial` protocol. There is deliberately no
per-API code (no TracyD3D12, TracyVulkan or TracyWebGPU ports), because the consumer is
expected to own the timestamp queries, and Evergine's low-level graphics layer already
abstracts those over every backend:

| the layer needs | Evergine low-level API |
|---|---|
| a timestamp query pool | `Factory.CreateQueryHeap`, `QueryType.Timestamp` |
| a timestamp per zone edge | `CommandBuffer.WriteTimestamp(heap, index)` |
| the results after the frame | `QueryHeap.ReadData(start, count, results)` |
| ns per GPU tick | `1e9f / graphicsContext.TimestampFrequency` |
| a GPU/CPU clock pair, same instant | `CommandQueue.GetClockCalibration(out gpu, out cpu)` |

```csharp
// startup: a simultaneous GPU/CPU pair anchors a calibrated context. Create runs right
// after the sample: Tracy stamps the CPU side itself, as "now".
commandQueue.GetClockCalibration(out ulong gpuNow, out long cpuNow);
var gpu = GpuProfilerContext.Create("frame GPU", TracyGpuContextType.Direct3D12,
    (long)gpuNow, 1e9f / graphicsContext.TimestampFrequency, queryCapacity: 64, calibrated: true);

// record time: reserve two query ids, write real timestamps against them
var zone = gpu.BeginZone("shadow pass");
commandBuffer.WriteTimestamp(queryHeap, zone.BeginQueryId);
// ... draw ...
commandBuffer.WriteTimestamp(queryHeap, zone.EndQueryId);
zone.End();

// after the frame's fence: heap slots map 1:1 to query ids when capacities match
queryHeap.ReadData(zone.BeginQueryId, 2, results);
gpu.SubmitTime(zone.BeginQueryId, (long)results[zone.BeginQueryId]);
gpu.SubmitTime(zone.EndQueryId, (long)results[zone.EndQueryId]);

// every few hundred frames: a fresh pair, and how far the CPU clock moved since the last one
commandQueue.GetClockCalibration(out gpuNow, out long cpu);
gpu.Calibrate((long)gpuNow, cpuDeltaNs: (long)((cpu - previousCpu) * 1e9 / Stopwatch.Frequency));
```

Size the query heap with the same capacity as the context (an even one) and drain
(`SubmitTime`) at least as fast as you emit: the ids are ring indices and wrap. Pairs are
aligned to even ids, so a zone's two slots are adjacent and one `ReadData(begin, 2, ...)`
covers both; `results` has to be as long as the heap, because every backend writes query
`i` at `results[i]`. Read a zone only once its frame's fence has been waited on: `ReadData`
returning false means "not ready yet" on Vulkan and OpenGL, and DirectX 12 returns true
unconditionally, so the fence is the guarantee, not the return value. Vulkan also resets a
slot as it is read, which is what lets the ring reuse it.

Two schemes keep the GPU track on the CPU timeline:

- **Calibrated** (`calibrated: true`, DirectX 12, Vulkan and desktop OpenGL in Evergine):
  the context is anchored on a pair sampled at the same instant by the driver, and
  `Calibrate` re-anchors it with a fresh pair every few hundred frames. The viewer maps
  every GPU timestamp through the pair and the measured ratio of both clocks, so the
  track stays put however far the GPU runs behind the CPU. The CPU delta handed to
  `Calibrate` is in nanoseconds and comes from the same clock as the pair, which with Evergine is
  `Stopwatch` ticks scaled by `1e9 / Stopwatch.Frequency`.
- **Uncalibrated** (the default, and the only option where `IsClockCalibrationSupported`
  is false): the context is anchored once, on a timestamp read back after a
  `Submit`/`WaitIdle`, and `TimeSync` can re-anchor it, but only with a GPU timestamp
  that is current at the moment of the call, since Tracy stamps the CPU side as "now". A
  timestamp read back from an earlier frame shifts the whole track by the age of that
  frame, which on a frames-in-flight loop is a frame or more. The sample therefore never
  re-syncs in this mode and reports the width of the anchor window instead.

## Samples

`LowLevelFormsSample` is a Windows Forms window drawing N cubes through a DirectX 12 or Vulkan
swap chain on Evergine's low-level graphics API, with three frames in flight synchronised by
fences and vsync off, instrumented end to end. It is where the GPU layer above meets an actual
GPU. The smoke test drives it with a synthetic clock, which cannot catch a mistake in how
timestamps are collected, nor whether the GPU track lands where it should.

```bash
dotnet run --project LowLevelFormsSample -c Release -- --backend vulkan
```

`--backend dx12|vulkan` picks the API (default `dx12`), `--no-calibration` forces the
uncalibrated scheme where the backend could calibrate, so both can be captured on the same
machine and compared, and `--validation` enables the graphics debug layers. Vulkan gets the
same HLSL compiled to SPIR-V at startup through DXC, because Evergine's Vulkan backend takes
bytecode only. The sample needs an Evergine with `Fence` and
`CommandQueue.GetClockCalibration`; see [Development](#development) for building it against
the Engine sources until those ship in a package.

The cube count is a slider because the thing being measured is how long a frame spends
*recording* its command buffer: more cubes is more draw calls and no other change, so the
`RecordCommands` zone has to move with it. `--cubes N` sets the starting count, which makes the
same comparison repeatable without touching the window. Measured on one machine, per draw call,
with the debug layers off (`--validation` turns them on; with them a draw costs about 10 µs
to record, which is the debug layer's cost and not the engine's):

| backend | cubes | `RecordCommands` mean | `DrawCalls` mean | per draw |
|---|---|---|---|---|
| DirectX 12 | 512 | 42.7 µs | 20.6 µs | 40 ns |
| DirectX 12 | 4096 | 187.8 µs | 141.3 µs | 34 ns |
| Vulkan | 512 | 38.6 µs | 24.2 µs | 47 ns |
| Vulkan | 4096 | 215.8 µs | 177.3 µs | 43 ns |

What to check once a viewer is attached, since the status bar reports the connection, so the app
tells you without switching windows:

- Zones carry their **names** (`Frame`, `FenceWait`, `Update`, `RecordCommands`, `DrawCalls`,
  `Submit`, `Present`) and resolve to `Program.cs`. Anything arriving as `???` means the
  source-location path broke.
- `RecordCommands` scales with the slider, and its width agrees with the `record ms` plot and
  the status bar. Three numbers from three paths; they have to match.
- Every zone carries its own **color**, set at the `BeginZone` call site, and `RecordCommands`
  turns red past its budget while `DrawCalls` renames itself to the cube count: the two
  reactive paths, `zone.Color()` and `zone.Name()`, both driven by the slider. An over-budget
  frame also drops a red line in the **Messages** window, at most one a second.
- The **GPU** track (`DirectX12 frame` or `Vulkan frame`) shows one closed zone per frame.
  Zones left open are timestamps that never arrived.
- Each `GPU frame` zone starts **after the `Submit` zone of the same frame**, never before it
  and never a whole frame later. With three frames in flight and vsync off the GPU trails the
  CPU, so a track that merely "looks close" is not evidence; the check that is evidence pairs
  the two series frame by frame (`tracy-csvexport -u -f Submit` against `tracy-csvexport -g`)
  and requires `gpuStart - submitStart` to be positive and small. The first message in the
  **Messages** window says which scheme the run used and how wide the startup anchor window
  was. Measured on one machine, 8 s captures at ~1000 fps (see the table below).
- The trace starts before the viewer connected. That history is what `TRACY_ON_DEMAND` being
  off buys, and losing it is a regression.

| run | frames | median `gpuStart - submitStart` | p95 | negatives |
|---|---|---|---|---|
| DirectX 12, calibrated, 512 cubes | 55,933 | 0.037 ms | 1.586 ms | 0 |
| DirectX 12, calibrated, 4096 cubes | 23,344 | 0.044 ms | 0.575 ms | 0 |
| Vulkan, calibrated, 512 cubes | 50,726 | 0.080 ms | 0.249 ms | 0 |
| Vulkan, calibrated, 4096 cubes | 19,389 | 0.086 ms | 0.359 ms | 0 |
| DirectX 12, `--no-calibration`, 512 cubes | 57,032 | 1.198 ms | 2.726 ms | 0 |
| Vulkan, `--no-calibration`, 512 cubes | 50,987 | 1.690 ms | 1.840 ms | 0 |

The calibrated offset is the GPU's own latency to pick up a submission and does not move
when the frame gets 2.5x longer, which is what "aligned" means here. The uncalibrated runs
sit a constant 1.2 ms (DirectX 12) and 1.7 ms (Vulkan) late: the startup anchor window
those runs reported was 1.9 ms and 3.8 ms wide, and the error is bounded by it, as the
message says. That constant is the reason the calibrated scheme exists: it is small
against a 16 ms frame and dwarfs a 0.2 ms one.

The client records from process start and buffers until a viewer connects, so this sample
accumulates memory if left running unattached.

## Scope

v1 binds the **CPU client** (zones with per-call-site and per-instance names and colors,
frames, plots, messages, thread names, app info, memory) plus the GPU emission layer above.
Locks are roadmap.

## Development

`Tracy.NET.slnx` holds all four projects. Building it needs one step first, because `SmokeTest`
consumes the **package** rather than the project, deliberately, since what it tests is whether
the `.nupkg` carries a native per runtime identifier:

```bash
dotnet pack Evergine.Bindings.Tracy/Evergine.Bindings.Tracy.csproj -c Release -p:Version=0.0.1-local -o SmokeTest/local-packages
dotnet build Tracy.NET.slnx -c Release
```

Skipping the pack fails with `NU1301` naming the missing `SmokeTest/local-packages` folder.
Note that re-packing the same version does not always take effect: NuGet resolves from the
global packages folder before consulting any source, so a stale `0.0.1-local` extracted there
shadows a fresh one. Delete `~/.nuget/packages/evergine.bindings.tracy/0.0.1-local` when the
smoke test compiles against an API that no longer matches the source.

Every project also builds standalone by path: the solution is a convenience, not a
requirement, and CI builds by explicit project path.

### Generate bindings locally

```bash
dotnet run --project TracyGen/TracyGen.csproj
```

### Run the low-level sample against a viewer

The sample needs `Fence` and `CommandQueue.GetClockCalibration`, which no released Evergine
package carries yet. There are two ways to get an Evergine that has them, and both are one
property on the command line.

**From a pull request build**, which needs no Engine checkout. Every pull request on
EvergineTeam/Engine gets a comment with a link to its build; install those packages into the
local feed and pass the version:

```powershell
.\Install-LocalFeed.ps1 -BasePath $env:EvergineLocalFeedPath -ActionUrl <the run url from the comment>
```

```bash
dotnet run --project LowLevelFormsSample -c Release -p:EvergineVersion=2026.9.9.1243-pr591 -- --backend vulkan --cubes 4096
```

Take the version from the run you installed rather than copying the one above: it carries the
minute of the day, so every CI run produces a different one. `Install-LocalFeed.ps1` lives in
the `scripts` folder of the Engine repository and needs an interactive console, since it draws
its own download progress.

**From an Engine checkout**, which is what guarantees the build matches a branch exactly. Point
`EvergineSourceRoot` at its `src` folder and the five Evergine projects are built from source
(the property is also read from the environment variable of the same name):

```bash
dotnet run --project LowLevelFormsSample -c Release -p:EvergineSourceRoot=C:\repositories\Engine\src -- --backend vulkan --cubes 4096
```

Either way, `dotnet build Tracy.NET.slnx` needs the same property; CI does not build the sample.

### Build the binding library

```bash
dotnet build Evergine.Bindings.Tracy/Evergine.Bindings.Tracy.csproj
```

### Run the smoke test against a locally packed package

```bash
dotnet pack Evergine.Bindings.Tracy/Evergine.Bindings.Tracy.csproj -c Release -p:Version=0.0.1-local -o SmokeTest/local-packages
dotnet run --project SmokeTest -c Release -r win-x64 --self-contained false -p:TracyVersion=0.0.1-local
```

## Workflows

| Workflow | Trigger | Description |
|----------|---------|-------------|
| CI | push/PR to `main` | Regenerate, build, and check native coherence |
| CD | monthly / manual | Resolve the tracked release, build the 5 native legs, pack, smoke-test, publish |
| API Gate | PR to `main` | Public API delta + every P/Invoke resolving in the shipped libraries |
| Sync standards | monthly / manual | Sync shared scripts from evergine-standards |

## License

The binding is MIT (see [LICENSE](LICENSE)). Tracy itself is BSD-3
([Tracy-LICENSE.txt](Tracy-LICENSE.txt)), which travels inside the package alongside the
binaries it covers.
