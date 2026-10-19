#!/usr/bin/env bash
# Builds libfreetype.a and libharfbuzz.a for browser-wasm with the workload's
# emcc. Usage: build.sh <freetype-src-dir> <harfbuzz-src-dir>
# Output: ./lib/libfreetype.so.6.a ./lib/libharfbuzz.so.0.a (git-ignored), named
# after the [LibraryImport] names in bibui, which is what the pinvoke table matches.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
. "$here/emenv.sh"
ft=$1; hb=$2
out="$here/lib"; obj="$here/obj"; mkdir -p "$out" "$obj"
# Same EH/sjlj model and SIMD as the .NET runtime (WasmEnableExceptionHandling,
# WasmEnableSIMD): FreeType longjmps, and mixing JS-based sjlj with the
# runtime's wasm EH does not link.
flags=(-O2 -msimd128 -fwasm-exceptions -sSUPPORT_LONGJMP=wasm)

ftdefs=(-DFT2_BUILD_LIBRARY "-DFT_CONFIG_MODULES_H=<ftmodule.h>" -I"$here" -I"$ft/include")
ftsrc=(base/ftsystem.c base/ftinit.c base/ftdebug.c base/ftbase.c base/ftbitmap.c base/ftglyph.c base/ftbbox.c base/ftmm.c gzip/ftgzip.c
       truetype/truetype.c sfnt/sfnt.c smooth/smooth.c autofit/autofit.c psnames/psnames.c)
rm -f "$obj"/*.o
for s in "${ftsrc[@]}"; do emcc "${flags[@]}" "${ftdefs[@]}" -c "$ft/src/$s" -o "$obj/ft_$(basename "$s" .c).o"; done
emcc "${flags[@]}" -c "$here/wasm_sjlj_shim.c" -o "$obj/ft_sjlj_shim.o"
rm -f "$out"/libfreetype*.a "$out/libfreetype.so.6.a"; emar rcs "$out/libfreetype.so.6.a" "$obj"/ft_*.o

em++ "${flags[@]}" -std=c++11 -fno-exceptions -fno-rtti -fno-threadsafe-statics -DHB_NO_MT \
  -c "$hb/src/harfbuzz.cc" -o "$obj/hb.o"
rm -f "$out/libharfbuzz.so.0.a"; emar rcs "$out/libharfbuzz.so.0.a" "$obj/hb.o"
ls -la "$out"
