#!/usr/bin/env bash
# Publishes the docs demos into dist/: bibui-demos.js, bibui-host.js and
# _framework/ (fingerprinted names; dotnet.js is the one that is not, and the
# boot manifest inside it names the rest). No pdbs, maps or .br/.gz: the
# docs site's CDN compresses. AOT=1 publishes AOT-compiled instead.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet-wasm}"
export PATH="$DOTNET_ROOT:$PATH"
lib="$here/../browser-host/native/lib"
[[ -f $lib/libfreetype.so.6.a && -f $lib/libharfbuzz.so.0.a ]] ||
  { echo "Build FreeType and HarfBuzz first: ../browser-host/native/build.sh <freetype-src> <harfbuzz-src>" >&2; exit 1; }
out="$here/obj/publish"
# From clean: an AOT publish strips the IL from the assemblies it leaves in
# obj/, and a later interpreter publish would pick those up and fail to start.
rm -rf "$out" "$here/obj/Release" "$here/bin/Release"
dotnet publish "$here/docs-demos.csproj" -c Release -o "$out" -p:RunAOTCompilation="${AOT:+true}" "$@"
rm -rf "$here/dist"
mkdir -p "$here/dist"
(cd "$out/wwwroot" && find . -type f ! -name '*.map' ! -name '*.pdb' ! -name '*.br' ! -name '*.gz' -print0 |
  xargs -0 cp --parents -t "$here/dist")
(cd "$here/dist" && find . -type f -printf '%s\t%P\n' | sort -k2)
du -sb "$here/dist"
