#!/usr/bin/env bash
# Builds FreeType and HarfBuzz for iOS as freetype.xcframework and
# harfbuzz.xcframework: each a dynamic framework for the device (ios-arm64) and
# one for the simulator (ios-arm64-simulator).
#
# bibui's text engine calls FreeType and HarfBuzz through P/Invoke
# (libfreetype.so.6 and libharfbuzz.so.0 on Linux, from the system). iOS has
# neither, so the iOS platform ships these in the app's Frameworks folder and
# points bibui's imports at them (TextLibraries.cs). They are the releases
# bibui's parity suite checks against. FreeType is built with no optional
# dependencies: bibui loads plain TrueType from memory and needs no zlib,
# bzip2, PNG, Brotli or HarfBuzz inside FreeType. HarfBuzz is its amalgamated
# harfbuzz.cc, with no FreeType, CoreText or ICU integration: bibui hands it
# the font's bytes.
#
# Usage: build-text-natives.sh [output dir]   (default: ./out next to this script)
# Needs Xcode (xcrun, xcodebuild), cmake and git.
set -euo pipefail

FREETYPE_TAG=VER-2-14-3
HARFBUZZ_TAG=14.4.0
MIN_IOS=15.0

here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here/out}
work=$out/work
mkdir -p "$work"

fetch() {
  local repo=$1 tag=$2 dir=$3
  [ -d "$dir" ] || git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$tag" "$repo" "$dir"
}
fetch https://github.com/freetype/freetype.git "$FREETYPE_TAG" "$work/freetype"
fetch https://github.com/harfbuzz/harfbuzz.git "$HARFBUZZ_TAG" "$work/harfbuzz"

# Wraps a dylib as <name>.framework in $work/<slice>/, named by its @rpath.
wrap_framework() {
  local dylib=$1 name=$2 slice=$3 version=$4
  local fw=$work/$slice/$name.framework
  rm -rf "$fw" && mkdir -p "$fw"
  cp "$dylib" "$fw/$name"
  install_name_tool -id "@rpath/$name.framework/$name" "$fw/$name"
  cat > "$fw/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>$name</string>
  <key>CFBundleIdentifier</key><string>org.$name.$name</string>
  <key>CFBundleName</key><string>$name</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>MinimumOSVersion</key><string>$MIN_IOS</string>
</dict>
</plist>
EOF
}

build_freetype() {
  local sdk=$1 slice=$2 build=$work/freetype-$2
  cmake -S "$work/freetype" -B "$build" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT="$sdk" \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=$MIN_IOS \
    -DBUILD_SHARED_LIBS=ON \
    -DFT_DISABLE_ZLIB=ON -DFT_DISABLE_BZIP2=ON -DFT_DISABLE_PNG=ON \
    -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_BROTLI=ON >/dev/null
  cmake --build "$build" --parallel >/dev/null
  wrap_framework "$(find "$build" -name 'libfreetype.*.dylib' -type f | head -1)" freetype "$slice" "${FREETYPE_TAG#VER-}"
}

build_harfbuzz() {
  local sdk=$1 slice=$2 target=$3 build=$work/harfbuzz-$2
  mkdir -p "$build"
  xcrun --sdk "$sdk" clang++ -std=c++17 -O2 -fno-exceptions -fno-rtti -DNDEBUG \
    -target "$target" -dynamiclib -install_name "@rpath/harfbuzz.framework/harfbuzz" \
    "$work/harfbuzz/src/harfbuzz.cc" -o "$build/libharfbuzz.dylib"
  wrap_framework "$build/libharfbuzz.dylib" harfbuzz "$slice" "$HARFBUZZ_TAG"
}

build_freetype iphoneos device
build_freetype iphonesimulator simulator
build_harfbuzz iphoneos device "arm64-apple-ios$MIN_IOS"
build_harfbuzz iphonesimulator simulator "arm64-apple-ios$MIN_IOS-simulator"

for name in freetype harfbuzz; do
  rm -rf "$out/$name.xcframework"
  xcodebuild -create-xcframework \
    -framework "$work/device/$name.framework" \
    -framework "$work/simulator/$name.framework" \
    -output "$out/$name.xcframework" >/dev/null
  echo "$out/$name.xcframework"
done
