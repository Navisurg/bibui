#!/usr/bin/env bash
# Builds the FreeType and HarfBuzz static libraries that bibui's text needs in
# the browser, for browser-wasm, with the emcc of the .NET wasm-tools workload:
#
#   bibui-platform-browser/native/build.sh
#
# Downloads the releases pinned in versions.env, checks their SHA-256, and
# writes lib/libfreetype.so.6.a and lib/libharfbuzz.so.0.a (git-ignored).
# Nothing built here is committed: CI builds the archives, and the
# Bibui.Platform.Browser package carries them.
#
# The names are bibui's [LibraryImport] names with .a added: the browser app's
# native link looks a P/Invoke's library up by the archive's file name without
# its extension, so libfreetype.so.6 must be libfreetype.so.6.a.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
. "$here/versions.env"
. "$here/emenv.sh"

src="$here/obj/src"; obj="$here/obj/o"; out="$here/lib"
mkdir -p "$here/obj/download" "$src" "$obj" "$out"

fetch() { # url sha256 -> path of the verified tarball
  local file="$here/obj/download/$(basename "$1")"
  if [[ ! -f $file ]] || ! echo "$2  $file" | sha256sum -c --status; then
    curl -fsSL --retry 3 -o "$file.part" "$1"
    if ! echo "$2  $file.part" | sha256sum -c --status; then
      echo "SHA-256 of $1 is $(sha256sum "$file.part" | cut -d' ' -f1), not $2" >&2
      rm -f "$file.part"; exit 1
    fi
    mv "$file.part" "$file"
  fi
  echo "$file"
}
ft_tar=$(fetch "$FREETYPE_URL" "$FREETYPE_SHA256")
hb_tar=$(fetch "$HARFBUZZ_URL" "$HARFBUZZ_SHA256")
rm -rf "$src"/*
tar -xJf "$ft_tar" -C "$src"
tar -xJf "$hb_tar" -C "$src"
ft="$src/freetype-$FREETYPE_VERSION"; hb="$src/harfbuzz-$HARFBUZZ_VERSION"

# The .NET runtime's own model: WebAssembly exception handling, with setjmp
# and longjmp on it (FreeType longjmps out of its rasterizer), and SIMD. A
# library built for Emscripten's JavaScript-based setjmp does not link beside
# the runtime. The prefix maps keep the build machine's paths out of the archives.
flags=(-O2 -msimd128 -fwasm-exceptions -sSUPPORT_LONGJMP=wasm
       "-ffile-prefix-map=$src=/_/src" "-ffile-prefix-map=$here=/_/native")

# FreeType with only what bibui's text uses: TrueType outlines (the embedded
# Inter), the autofitter, the smooth rasterizer (ftmodule.h), and the base
# with multiple-masters and gzip, which the sfnt and TrueType drivers call.
ftdefs=(-DFT2_BUILD_LIBRARY "-DFT_CONFIG_MODULES_H=<ftmodule.h>" -I"$here" -I"$ft/include")
ftsrc=(base/ftsystem.c base/ftinit.c base/ftdebug.c base/ftbase.c base/ftbitmap.c base/ftglyph.c
       base/ftbbox.c base/ftmm.c gzip/ftgzip.c
       truetype/truetype.c sfnt/sfnt.c smooth/smooth.c autofit/autofit.c psnames/psnames.c)
rm -f "$obj"/*.o
for s in "${ftsrc[@]}"; do
  emcc "${flags[@]}" "${ftdefs[@]}" -c "$ft/src/$s" -o "$obj/ft_$(basename "$s" .c).o"
done
emcc "${flags[@]}" -c "$here/wasm_sjlj_shim.c" -o "$obj/ft_sjlj_shim.o"

# HarfBuzz as one translation unit, single-threaded, no C++ exceptions or RTTI.
em++ "${flags[@]}" -std=c++11 -fno-exceptions -fno-rtti -fno-threadsafe-statics -DHB_NO_MT \
  -c "$hb/src/harfbuzz.cc" -o "$obj/hb.o"

# emar writes deterministic archives: no timestamps, owners or modes.
rm -f "$out"/*
(cd "$obj" && emar rcs "$out/libfreetype.so.6.a" ft_*.o && emar rcs "$out/libharfbuzz.so.0.a" hb.o)
# The licences travel with the archives into the package (licenses/).
cp "$ft/docs/FTL.TXT" "$out/FreeType-FTL.txt"
cp "$hb/COPYING" "$out/HarfBuzz-COPYING.txt"
(cd "$out" && sha256sum *.a)
