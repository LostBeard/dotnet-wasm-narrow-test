# dotnet-wasm-narrow-test

**Test your browser: https://lostbeard.github.io/dotnet-wasm-narrow-test/**

In Blazor WebAssembly, `Vector128.Narrow(Vector128<ushort>, Vector128<ushort>)` returns **saturated** bytes instead of truncated ones once the loop it sits in
is hot. `Vector128.Narrow` is documented to truncate ("the default conversion behavior for `ushort` to `byte`, which is truncation"; saturation is a separate
method, `NarrowWithSaturation`): `0xADDB` narrowed to 8 bits is `0xDB`. In an affected build it comes back `0x00`, and `0x455B` comes back `0xFF`.

**Already reported to .NET:** [dotnet/runtime#136688](https://github.com/dotnet/runtime/issues/136688) (opened 2026-10-30 by another project, which hit it under
Node; open on 2026-11-18). We found it independently and found that report afterwards. This repository adds a page that runs in a browser, the .NET 8 and 9
results, a control with only the hot-path compiler turned off, and where in the runtime's source it comes from.

The page runs the narrowing in loops of 100,000 rounds, compares every value with a plain integer cast, and shows what it found. It is plain .NET: one small
Blazor WebAssembly app, no library besides the base class library. [NarrowCases.cs](NarrowCases.cs) is all of the test code.

## What we measured

Windows 11 x64, headless Chromium 153 and Chrome 157, 2026-11-18. The counts are identical on every run.

| Build of the same code | .NET runtime | Result |
|---|---|---|
| **Default** (the interpreter with its hot-path compiler, the "jiterpreter": what a plain `dotnet publish` of a Blazor WebAssembly app is) | 10.0.14 | **Affected** |
| Default, with the runtime as shipped (`-p:WasmBuildNative=false`: no relinking by the `wasm-tools` workload) | 10.0.14 | **Affected**, the same counts |
| Default | 9.0.22 | **Affected** |
| Default | 8.0.33 | **Affected** |
| Interpreter only (`<BlazorWebAssemblyJiterpreter>false</BlazorWebAssemblyJiterpreter>`) | 10.0.14 | Not affected |
| Ahead-of-time compiled (`<RunAOTCompilation>true</RunAOTCompilation>`) | 10.0.14 | Not affected |
| The same cases as a desktop console program (x64) | 10.0.14 | Not affected |

(The builds were made on a machine with the `wasm-tools` workload, which relinks the runtime on publish; the second row is the control for that.)

The published page is built by GitHub with whatever servicing release is current: on 2026-11-18 that was 10.0.15, 9.0.23 and 8.0.34, with the same results and
the same counts.

The .NET 10 default build, case by case:

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

- Only the unsigned 16 -> 8 bit overload is wrong. The 32 -> 8 bit cases fail because their second step is that overload. In .NET 9 and 8 the same case fails with
  the same count from the same round.
- It is right while the code is cold (the single call, and the first few hundred rounds of each loop) and wrong from then on.
- With the hot-path compiler off, or with ahead-of-time compilation, or on the desktop, the same code is right for every value.
- `ADDB` giving `00` and values above 255 giving `FF` is exactly what WebAssembly's `i8x16.narrow_i16x8_u` returns: it reads its input as signed 16-bit and
  saturates to 0..255.

## Where it comes from

In the runtime's source ([dotnet/runtime](https://github.com/dotnet/runtime), the same on `release/8.0`, `release/9.0`, `release/10.0` and `main` on 2026-11-18):

- `src/mono/mono/mini/interp/interp-simd-intrins.def` lists the interpreter's SIMD intrinsics with the WebAssembly opcode the hot-path compiler may emit for each:

  ```
  INTERP_SIMD_INTRINSIC_P_PP (INTERP_SIMD_INTRINSIC_V128_U1_NARROW, interp_v128_u1_narrow, 102)
  ```

  102 is 0x66, `i8x16.narrow_i16x8_u`: the saturating narrow (the same file gives 0x66 to `PackedSimd.ConvertNarrowingSaturateUnsigned`).
- `src/mono/mono/mini/interp/interp-simd.c`, `interp_v128_u1_narrow`, what the interpreter runs for the same intrinsic, truncates (`res_typed [i] = v1_typed [i]`).
  The same file builds `interp_simd_p_pp_wasm_opcode_table` from the third field of each entry, and `mono_jiterp_get_simd_opcode` (`interp.c`) hands it out.
- `src/mono/mono/mini/interp/transform-simd.c` gives `Narrow` an intrinsic only for a `byte` result, which is why the other overloads are not affected.
- `src/mono/browser/runtime/jiterpreter-trace-generator.ts`, `emit_simd_3`, asks for that opcode and emits it as it is.

So the interpreter and the code compiled from it disagree for this one intrinsic (this is our reading of the source; we have not built a runtime with the entry
changed). WebAssembly has no truncating narrow; a truncating form would be a mask
(`v128.and` with 0x00FF in every lane) before the same opcode, or an `i8x16.shuffle` that takes the even bytes.

## Until it is fixed

Mask to the narrow type's range before narrowing. Truncation and saturation then give the same byte:

```csharp
var low8 = Vector128.Create((ushort)0xFF);
Vector128.Narrow(a & low8, b & low8);
```

(The "masked" control above is this, and is right in every build.) Or turn the hot-path compiler off for the app with
`<BlazorWebAssemblyJiterpreter>false</BlazorWebAssemblyJiterpreter>`, at the cost of its speed.

## Results from other machines

If you run the page, please add your result (the page gives you a text report to copy), affected or not, to
[SpawnDev.ILGPU.ML issue #3](https://github.com/LostBeard/SpawnDev.ILGPU.ML/issues/3) or as an issue here. Other browsers, devices and .NET versions are what is
missing. The bug itself is tracked by .NET in [dotnet/runtime#136688](https://github.com/dotnet/runtime/issues/136688).

| .NET | Browser / OS | Default | Interpreter only | AOT | Who |
|---|---|---|---|---|---|
| 10.0.14 | headless Chromium 153, Windows 11 x64 | Affected | Not affected | Not affected | this repository |
| 10.0.14 | Chrome 157, Windows 11 x64 | Affected (the same counts) | | | this repository |
| 9.0.22 | headless Chromium 153, Windows 11 x64 | Affected | | | this repository |
| 8.0.33 | headless Chromium 153, Windows 11 x64 | Affected | | | this repository |
| 10.0.15 | headless Chromium 153, Windows 11 x64 (the published page) | Affected | Not affected | Not affected | this repository |
| 9.0.23 | headless Chromium 153, Windows 11 x64 (the published page) | Affected | | | this repository |
| 8.0.34 | headless Chromium 153, Windows 11 x64 (the published page) | Affected | | | this repository |

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
dotnet publish NarrowTest.csproj -c Release -o publish/net9 -p:Tfm=net9.0 -p:IntermediateOutputPath=obj/net9/ -p:OutputPath=bin/net9/
dotnet publish NarrowTest.csproj -c Release -o publish/net8 -p:Tfm=net8.0 -p:IntermediateOutputPath=obj/net8/ -p:OutputPath=bin/net8/
```

The `net8` build needs `dotnet workload install wasm-tools-net8` if the `wasm-tools` workload is installed. Serve a build's `wwwroot` folder with any static file
server and open it. The [workflow](.github/workflows/deploy-to-github-pages.yml) builds all five and puts
them on GitHub Pages: the .NET 10 default build at the root, the others under `interpreter-only/`, `aot/`, `net9/` and `net8/`.

## Licence

MIT.
