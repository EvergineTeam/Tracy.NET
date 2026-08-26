# Tracy.NET

This repository contains low-level bindings for the [Tracy profiler](https://github.com/wolfpld/tracy) CPU client used in [Evergine](https://evergine.com/).

[![CI](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CI.yml/badge.svg)](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CI.yml)
[![CD](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CD.yml/badge.svg)](https://github.com/EvergineTeam/Tracy.NET/actions/workflows/CD.yml)
[![Nuget](https://img.shields.io/nuget/v/Evergine.Bindings.Tracy?logo=nuget)](https://www.nuget.org/packages/Evergine.Bindings.Tracy)

## Purpose

Tracy is a real-time, nanosecond-resolution frame profiler. This package binds its C API
(`TracyC.h`) so a .NET application can be instrumented and inspected live from the
[Tracy viewer](https://github.com/wolfpld/tracy/releases) — zones, frames, plots, messages
and thread names.

Two layers ship in one package:

- **`Tracy`** (generated) — the raw 79 P/Invokes, byte-for-byte faithful to `TracyC.h`.
  Strings are `byte*` on purpose: Tracy retains several of those pointers and reads them
  later from the profiler thread, so no marshaller may free them behind its back.
- **`Profiler` / `ProfilerZone`** (hand-written) — the API you actually use. Source
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
site, and the value does not have to be a real address — Tracy accepts unique numeric ids,
which is how GPU or defragmenting allocators get tracked at all (the memory map is what you
give up).

Overloads taking a pool name track a separate pool, listed on its own in the memory window —
graphics-API memory apart from the general heap. `Profiler.MemDiscard(pool)` releases a whole
pool at once, which is the only kind of free an arena or bump allocator has.

```csharp
Profiler.MemAlloc(buffer.NativePointer, sizeInBytes, "gpu");
```

> **The books must balance.** Tracy *terminates the session* on a free without a matching
> allocation, on the same address allocated twice without a free in between, or on a double
> free. The relief Tracy grants to on-demand clients does not apply here — this package's
> natives are built without `TRACY_ON_DEMAND`, so the accounting must hold from the first event
> of the process. A capture that dies on its own is nearly always one of those three.

### Colors

`TracyColor` carries the palette from Tracy's own `TracyColor.hpp`, so a color is picked by
name rather than by remembering a hexadecimal. It is not a closed set — any RGB value works,
cast it: `(TracyColor)0x1a2b3c`.

Two things follow from how Tracy defines colors, both of which the API preserves rather than
papers over:

- `0` means **no color was set**, not black. A zone left at `TracyColor.None` keeps the color
  the viewer derives from its source location. Upstream declares `Black = 0x000000`, so
  `TracyColor.Black` is a synonym of `None`; for an actually black zone use
  `(TracyColor)0x000001`, which is what Tracy's manual recommends.
- The color and name passed to `BeginZone` belong to the **source location**, shared by every
  hit of that call site. `zone.Color()` and `zone.Name()` apply to **that one hit**, and are
  only valid while the zone is the innermost open one on the thread — Tracy's zone events form
  a stack, so anything emitted after a nested zone opened lands on the nested zone instead.

## How the native client is built

The package carries `TracyClient` compiled from the same upstream tag the bindings are
generated from, with two defines that are part of the contract:

- `TRACY_ENABLE` — without it the API does not exist at all.
- `TRACY_ON_DEMAND` is **off** — the client records continuously from process start, so
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
| `win-arm64` | **not executed** — no ARM64 runner in the matrix. The application is published for it and `TracyClient.dll` has to reach the output, so the evidence is the file, not a run |
| `osx-x64` | served by the same universal `osx` dylib as `osx-arm64`; the arm64 half is what gets executed in CI |

What no CI leg can verify: that a viewer receives sensible data. That is a human step in
the release process — connect Tracy v0.14 to the smoke test binary and watch zones arrive.

## GPU zones

The package also ships a graphics-API-agnostic GPU layer: `GpuProfilerContext` and
`GpuZone` over Tracy's `___tracy_emit_gpu_*_serial` protocol. There is deliberately no
per-API code — no TracyD3D12, TracyVulkan or TracyWebGPU ports — because the consumer is
expected to own the timestamp queries, and Evergine's low-level graphics layer already
abstracts those over every backend:

| the layer needs | Evergine low-level API |
|---|---|
| a timestamp query pool | `Factory.CreateQueryHeap`, `QueryType.Timestamp` |
| a timestamp per zone edge | `CommandBuffer.WriteTimestamp(heap, index)` |
| the results after the frame | `QueryHeap.ReadData(start, count, results)` |
| ns per GPU tick | `1e9f / graphicsContext.TimestampFrequency` |

```csharp
// startup: one raw timestamp + the tick period define the context
var gpu = GpuProfilerContext.Create("frame GPU", TracyGpuContextType.Direct3D12,
    initialGpuTimestamp, 1e9f / graphicsContext.TimestampFrequency, queryCapacity: 64);

// record time: reserve two query ids, write real timestamps against them
var zone = gpu.BeginZone("shadow pass");
commandBuffer.WriteTimestamp(queryHeap, zone.BeginQueryId);
// ... draw ...
commandBuffer.WriteTimestamp(queryHeap, zone.EndQueryId);
zone.End();

// after readback: heap slots map 1:1 to query ids when capacities match
gpu.SubmitTime(zone.BeginQueryId, (long)results[zone.BeginQueryId]);
gpu.SubmitTime(zone.EndQueryId, (long)results[zone.EndQueryId]);
```

Size the query heap with the same capacity as the context and drain (`SubmitTime`) at
least as fast as you emit — the ids are ring indices and wrap. `ReadData` returning false
means "not ready yet": retry next frame rather than discard. Contexts are uncalibrated
(the low-level layer exposes no CPU-GPU clock correlation), so call `TimeSync` with a
fresh raw timestamp every few hundred frames to keep the track anchored — the same scheme
Tracy's own OpenGL helper uses.

## Samples

`Dx12FormsSample` is a Windows Forms window drawing N cubes through a DirectX 12 swap chain on
Evergine's low-level graphics API, instrumented end to end. It is where the GPU layer above
meets an actual GPU — the smoke test drives it with a synthetic clock, which cannot catch a
mistake in how timestamps are collected.

```bash
dotnet run --project Dx12FormsSample -c Release
```

The cube count is a slider because the thing being measured is how long a frame spends
*recording* its command buffer: more cubes is more draw calls and no other change, so the
`RecordCommands` zone has to move with it. `--cubes N` sets the starting count, which makes the
same comparison repeatable without touching the window. Measured on one machine, per draw call:

| cubes | `RecordCommands` mean | `DrawCalls` mean | per draw |
|---|---|---|---|
| 128 | 280 µs | 244 µs | 1.91 µs |
| 4096 | 8.09 ms | 7.99 ms | 1.95 µs |

What to check once a viewer is attached — the status bar reports the connection, so the app
tells you without switching windows:

- Zones carry their **names** (`Frame`, `Update`, `RecordCommands`, `DrawCalls`, `Submit`,
  `Present`) and resolve to `Program.cs`. Anything arriving as `???` means the source-location
  path broke.
- `RecordCommands` scales with the slider, and its width agrees with the `record ms` plot and
  the status bar. Three numbers from three paths; they have to match.
- Every zone carries its own **color**, set at the `BeginZone` call site, and `RecordCommands`
  turns red past its budget while `DrawCalls` renames itself to the cube count — the two
  reactive paths, `zone.Color()` and `zone.Name()`, both driven by the slider. An over-budget
  frame also drops a red line in the **Messages** window, at most one a second.
- The **GPU** track `DX12 frame` shows one closed zone per frame. Zones left open are
  timestamps that never arrived.
- The trace starts before the viewer connected. That history is what `TRACY_ON_DEMAND` being
  off buys, and losing it is a regression.

The client records from process start and buffers until a viewer connects, so this sample
accumulates memory if left running unattached.

## Scope

v1 binds the **CPU client** (zones with per-call-site and per-instance names and colors,
frames, plots, messages, thread names, app info, memory) plus the GPU emission layer above.
Locks are roadmap.

## Development

`Tracy.NET.slnx` holds all four projects. Building it needs one step first, because `SmokeTest`
consumes the **package** rather than the project — deliberately, since what it tests is whether
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

Every project also builds standalone by path — the solution is a convenience, not a
requirement, and CI builds by explicit project path.

### Generate bindings locally

```bash
dotnet run --project TracyGen/TracyGen.csproj
```

### Run the DirectX 12 sample against a viewer

```bash
dotnet run --project Dx12FormsSample -c Release -- --cubes 4096
```

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
