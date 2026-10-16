# Performance

bibui runs on two stacks, and you choose one per app:

- **Silk + Skia**: a GLFW window (`Bibui.Platform.Silk`) drawn by SkiaSharp
  (`Bibui.Raster.Skia`), on the GPU when there is one.
- **Wayland + managed**: a raw Wayland window (`Bibui.Platform.Wayland`) drawn on
  the CPU by bibui's own rasterizer (`Bibui.Raster.Managed`), with FreeType and
  HarfBuzz for text. It runs on Linux only.

This page records what measuring them has shown. The numbers come from one
machine, a Ryzen AI 9 365 with a Radeon 880M running Hyprland, on .NET 10. Each
timing ran on a core with nothing else on it. Compare
the two stacks with each other, not with your own machine.

## End to end

The same small dashboard app was built on each stack and published with Native AOT.

| | Silk + Skia | Wayland + managed |
|---|---|---|
| AOT output | 15.1 MB, of which `libSkiaSharp.so` is 8.8 MB | **5.8 MB**, one file |
| Native libraries shipped | `libSkiaSharp.so`, `libglfw.so.3` | none (it loads the system's libwayland, libxkbcommon, FreeType and HarfBuzz) |
| Cold start, spawn to first frame (details form) | 17.1 ms | **9.9 ms** |
| RSS at first frame | 46.8 MB | **33.1 MB** |
| 600-button grid, frame p50, 1800×1400 | 6.37 ms | **4.61 ms** |
| 600-button grid, frame p50, 1280×800 @1× | 2.54 ms | 2.50 ms |

The frame rows are headless, so they cover build, layout and raster but not
present.

## In a window

- **Present.** The Wayland host draws into a shared-memory buffer, so presenting
  costs 0.01–0.03 ms and needs no GL upload or readback. On the GPU path the CPU
  side of a 600-button frame is about 5.5 ms p50, which records the frame and
  flushes it.
- **Pacing.** Wayland frame callbacks give an even 16.67 ms interval. The GLFW
  path's p99 interval is 17.5 ms.
- **Memory and threads.** On the same scene, running under the JIT: 71 MiB RSS
  and 7 threads on Wayland, against 179–199 MiB and 23 threads on Silk + Skia.
- **Whole-window redraws.** These are where the GPU wins. Per megapixel of CPU
  time, GL costs about 1.0 ms (GPU time not counted), Wayland shared memory
  about 2.4 ms and the Skia CPU fallback about 4.3 ms. A CPU-only stack holds
  60 Hz through damage tracking (`ManagedRenderer.DamageTracking`), which repaints
  only what changed. A typical frame changes a hover colour or a caret, not the
  whole window.

## Pixels

Both stacks are checked against the same Skia-rendered reference pictures of
every component. Text from FreeType and HarfBuzz measures exactly as Skia does,
and its pixels match Skia's (SSIM 1.0). The remaining differences on the managed
rasterizer are in gradients and wide strokes.

## Measuring

The numbers come from the maintainers' benchmark harness, which is not part of
this repository. `BIBUI_PROFILE=1` prints a per-render breakdown of time and
allocation by phase in any app. When you compare numbers, run on a quiet machine
and record the load average with them: on a busy machine the medians inflate and
p99 becomes mostly scheduler noise.
