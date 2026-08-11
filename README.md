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
- `TRACY_ON_DEMAND` — the client records **only while a viewer is connected**. An
  application running without one pays near-zero cost and does not grow memory. The flip
  side: to capture from the very first frame, connect the viewer before starting the
  workload.

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

## Scope

v1 binds the **CPU client**: zones, frames, plots, messages, thread names, app info.
GPU contexts (D3D/Vulkan timestamp queries), locks and memory hooks are roadmap.

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
