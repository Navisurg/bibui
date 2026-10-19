#!/usr/bin/env bash
# Builds the counter for an iPhone, signed as Signing.local.props says (see
# Signing.local.props.example), installs it on a connected iPhone and launches
# it.
#
# Usage: run-device.sh [device name or UDID]   (default: the first connected iPhone)
# The iPhone is paired with this Mac and in Developer Mode.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
if [ ! -f "$here/Signing.local.props" ]; then
  echo "No Signing.local.props: copy Signing.local.props.example to it and fill in your signing." >&2
  exit 1
fi
device=${1:-$(xcrun devicectl list devices 2>/dev/null | grep -E 'connected.*physical' | grep -m1 -oE '[0-9A-F]{8}-[0-9A-F]{16}|[0-9A-F-]{36}')}
if [ -z "$device" ]; then
  echo "No connected iPhone." >&2
  exit 1
fi
app=$here/bin/Debug/net10.0-ios/ios-arm64/counter-ios.app

dotnet build "$here" -r ios-arm64 -nologo -v:q
bundle=$(dotnet msbuild "$here" -p:RuntimeIdentifier=ios-arm64 -getProperty:ApplicationId)
xcrun devicectl device install app --device "$device" "$app"
xcrun devicectl device process launch --terminate-existing --device "$device" "$bundle"
