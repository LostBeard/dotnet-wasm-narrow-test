# dotnet-wasm-narrow-test

**Test your browser: https://lostbeard.github.io/dotnet-wasm-narrow-test/**

In Blazor WebAssembly on .NET 10, `Vector128.Narrow(Vector128<ushort>, Vector128<ushort>)` returns **saturated** bytes instead of truncated ones once the loop
it sits in is hot. `Vector128.Narrow` must truncate: `0xADDB` narrowed to 8 bits is `0xDB`. In the affected build it comes back `0x00`, and `0x455B` comes back
`0xFF`: the result of WebAssembly's `i8x16.narrow_i16x8_u`, which treats its input as signed and saturates to 0..255.

The page runs the narrowing in loops of 100,000 rounds, compares every value with a plain integer cast, and shows what it found. It is plain .NET: one small
Blazor WebAssembly app, no library besides the base class library ([NarrowCases.cs](NarrowCases.cs) is all of it).

## What we measured

.NET 10.0.14 (SDK 10.0.403), Windows 11, headless Chromium 153 (and the default build in Chrome 157, with the same counts), 2026-11-18. The same code built three ways:

| Build | Result |
|---|---|
| **Default** (the interpreter with its hot-path compiler, the "jiterpreter": what a plain `dotnet publish` of a Blazor WebAssembly app is) | **Affected** |
| **Interpreter only** (`<BlazorWebAssemblyJiterpreter>false</BlazorWebAssemblyJiterpreter>`) | Not affected |
| **Ahead-of-time compiled** (`<RunAOTCompilation>true</RunAOTCompilation>`) | Not affected |

In the default build (identical on every run):

| Case | Wrong values | First wrong round | First wrong value |
|---|---|---|---|
| One call, not hot: 32 -> 16 -> 8 bits (control) | 0 of 16 | | |
| Hot loop: `Narrow` 32 -> 16 -> 8 bits | 1,566,864 of 1,600,000 | 1,309 | word `1A79455B`: got `FF`, expected `5B` |
| Hot loop: `Narrow` 32 -> 16 bits only | 0 of 800,000 | | |
| **Hot loop: `Narrow` 16 -> 8 bits only (unsigned)** | **1,582,016 of 1,600,000** | **343** | word `ADDB`: got `00`, expected `DB` |
| Hot loop: `Narrow` 32 -> 16 bits, signed | 0 of 800,000 | | |
| Hot loop: `Narrow` 16 -> 8 bits, signed | 0 of 1,600,000 | | |
| Hot loop: `Narrow` 64 -> 32 bits | 0 of 400,000 | | |
| Hot loop: as found (256-bit vectors, a shift / xor mix, then narrowed to bytes) | 1,565,926 of 1,600,000 | 1,309 | mixed word `0A3E187C`: got `FF`, expected `7C` |
| Hot loop: masked to 0..255 before narrowing (control) | 0 of 1,600,000 | | |
| Hot loop: values that already fit, 0..255 (control) | 0 of 1,600,000 | | |

So:

- Only the unsigned 16 -> 8 bit overload is wrong. The 32 -> 8 bit cases fail because their second step is that overload.
- It is right while the code is cold (the single call, and the first few hundred rounds of each loop) and wrong from then on.
- With the hot-path compiler off, or with ahead-of-time compilation, the same code is right for every value.

That points at the code the hot-path compiler generates for this one overload. We have not read the runtime's source to confirm where.

## Until it is fixed

Mask to the narrow type's range before narrowing. Truncation and saturation then give the same byte:

```csharp
var low8 = Vector128.Create((ushort)0xFF);
Vector128.Narrow(a & low8, b & low8);
```

(The "masked" control above is this, and is right in every build.) Or turn the hot-path compiler off for the app with
`<BlazorWebAssemblyJiterpreter>false</BlazorWebAssemblyJiterpreter>`, at the cost of its speed.

## Results from other machines

If you run the page, please add your result (the page gives you a text report to copy), affected or not:
[SpawnDev.ILGPU.ML issue #3](https://github.com/LostBeard/SpawnDev.ILGPU.ML/issues/3). Other .NET versions, browsers and devices are what is missing.

| .NET | Browser / OS | Default | Interpreter only | AOT | Who |
|---|---|---|---|---|---|
| 10.0.14 | headless Chromium 153, Windows 11 x64 | Affected | Not affected | Not affected | this repository |
| 10.0.14 | Chrome 157, Windows 11 x64 | Affected (the same counts) | | | this repository |

## How it was found

[SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML) vectorized a random byte stream (the low byte of each 32-bit word of an MT19937 generator).
On the desktop it matched the plain generator word for word; in the browser it returned `FF` for almost every draw. Three one-shot probes of `Vector128.Narrow` in the
browser all said it truncates; only a probe that ran hot showed the wrong values. The library masks before narrowing (its
[workarounds page](https://github.com/LostBeard/SpawnDev.ILGPU.ML/blob/master/Docs/workarounds.md)) and carries this loop as a canary in its test suite.

## Build it yourself

```
dotnet publish NarrowTest.csproj -c Release -o publish/default
dotnet publish NarrowTest.csproj -c Release -o publish/interpreter-only -p:Jiterpreter=false -p:IntermediateOutputPath=obj/interpreter-only/ -p:OutputPath=bin/interpreter-only/
dotnet publish NarrowTest.csproj -c Release -o publish/aot -p:Aot=true -p:IntermediateOutputPath=obj/aot/ -p:OutputPath=bin/aot/     (needs: dotnet workload install wasm-tools)
```

Serve a build's `wwwroot` folder with any static file server and open it. The [workflow](.github/workflows/deploy-to-github-pages.yml) builds all three and puts
them on GitHub Pages: the default build at the root, the others under `interpreter-only/` and `aot/`.

## Licence

MIT.
