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
    using (var zone = Profiler.BeginZone("update"))
    {
        zone.Text(sceneName);
        Update();
    }

    Profiler.Plot("entities", entityCount);
    Profiler.FrameMark();
}
```

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

## Scope

v1 binds the **CPU client** (zones, frames, plots, messages, thread names, app info) plus
the GPU emission layer above. Locks and memory hooks are roadmap.

## Development

### Generate bindings locally

```bash
dotnet run --project TracyGen/TracyGen.csproj
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
