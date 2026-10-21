#!/usr/bin/env bash
# Pack and publish all Bibui library packages to the local NuGet feed.
# Usage: ./nuget-publish.sh [--version 1.2.3]

set -euo pipefail

# NUGET_FEED=DIR packs into another directory instead (a dry run that leaves the feed alone).
FEED="${NUGET_FEED:-$HOME/.nuget/local-packages}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
VERSION=""

# Derive worktree suffix from the directory name (e.g. "sharp-camels-peel-8s4")
# This becomes a NuGet prerelease tag so each worktree gets a unique version.
WORKTREE_NAME="$(basename "$SCRIPT_DIR")"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    *) echo "Unknown argument: $1"; exit 1 ;;
  esac
done

# If no explicit version, build one from the csproj Version + worktree name
if [[ -z "$VERSION" ]]; then
  BASE_VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$SCRIPT_DIR/Directory.Build.props")"
  VERSION="${BASE_VERSION:-1.0.0}-${WORKTREE_NAME}"
fi

PROJECTS=(
  "bibui-core/bibui-core.csproj"
  "bibui-gl/bibui-gl.csproj"
  "bibui-raster-skia/bibui-raster-skia.csproj"
  "bibui-platform-silk/bibui-platform-silk.csproj"
  "bibui-testing/bibui-testing.csproj"
  # The raw Wayland host and the managed raster backend
  # with its FreeType/HarfBuzz text — an app on these ships no Skia or Silk.
  "bibui-raster-managed/src/Bibui.Raster.Managed.csproj"
  "bibui-raster-managed/backend/Bibui.Raster.Managed.Backend.csproj"
  "bibui-platform-wayland/bibui-platform-wayland.csproj"
  # The browser host, with FreeType and HarfBuzz built for the browser
  # (bibui-platform-browser/native/build.sh; skipped below until they are).
  "bibui-platform-browser/bibui-platform-browser.csproj"
  # A bibui app in one package: bibui, a window and a renderer (docs/app-packages.md).
  "bibui-apps/silk-skia-gl/bibui-app-silk-skia-gl.csproj"
  "bibui-apps/silk-skia-cpu/bibui-app-silk-skia-cpu.csproj"
  "bibui-apps/silk-managed/bibui-app-silk-managed.csproj"
  "bibui-apps/wayland-managed/bibui-app-wayland-managed.csproj"
  "bibui-apps/wayland-skia-cpu/bibui-app-wayland-skia-cpu.csproj"
  # Bibui: all of the above in one reference, the default app among them.
  "bibui-all/bibui-all.csproj"
)

# A layer kept in its own top-level directory lists its packages in
# <dir>/nuget-projects, one project path per line relative to this script, so
# this script names none of them and works the same when the directory is gone.
for list in "$SCRIPT_DIR"/*/nuget-projects; do
  [[ -f "$list" ]] || continue
  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ -z "$line" || "$line" == \#* ]] && continue
    PROJECTS+=("$line")
  done < "$list"
done

for proj in "${PROJECTS[@]}"; do
  # Bibui depends on the browser package too, so it waits for it.
  if [[ ($proj == bibui-platform-browser/* || $proj == bibui-all/*) && ! -f "$SCRIPT_DIR/bibui-platform-browser/native/lib/libfreetype.so.6.a" ]]; then
    echo "==> Skipping $proj: run bibui-platform-browser/native/build.sh first (it needs the wasm-tools workload)"
    continue
  fi
  echo "==> Packing $proj"
  args=("dotnet" "pack" "$SCRIPT_DIR/$proj" "--configuration" "Release" "--output" "$FEED")
  if [[ -n "$VERSION" ]]; then
    args+=("/p:Version=$VERSION")
  fi
  "${args[@]}"
done

echo ""
echo "Packages published to: $FEED"
echo ""
echo "To use in another project, add:"
echo "  <PackageReference Include=\"Bibui\" Version=\"$VERSION\" />"
echo "  or, for the fewest dependencies, the one Bibui.App package the app runs on (docs/app-packages.md)"
echo "  for a loop of your own; Bibui.Testing for headless tests."
echo "  samples/counter is the whole loop."
