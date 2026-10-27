# FreeType and HarfBuzz for the browser

On the desktop, bibui loads FreeType and HarfBuzz from the system
(`libfreetype.so.6`, `libharfbuzz.so.0`). A browser has no system libraries,
so the package carries both as WebAssembly static libraries, built by
`bibui-platform-browser/native/build.sh` from release tarballs pinned by
version and SHA-256, with the workload's own Emscripten, and its targets add
them to the app's link (`NativeFileReference`). They are built as the .NET
runtime is: WebAssembly exception handling, with setjmp and longjmp on it,
and SIMD.

The app's link finds a P/Invoke's library by the static library's file name
without its extension, so the archives are named after bibui's import names:
`libfreetype.so.6.a` and `libharfbuzz.so.0.a`. That leaves bibui's imports,
and the desktop routes that load the system libraries, exactly as they are.

`build.sh` builds them into `lib/` (git-ignored); `versions.env` pins the
releases and the Emscripten. CI builds the archives, and the
`Bibui.Platform.Browser` package carries them.
