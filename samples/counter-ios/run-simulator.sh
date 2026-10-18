#!/usr/bin/env bash
# Builds the counter for the iOS simulator, boots a simulator, installs the app
# and launches it with its console here (Ctrl-C stops it).
#
# Usage: run-simulator.sh [device name or UDID]   (default: the first available iPhone)
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
device=${1:-$(xcrun simctl list devices available | grep -m1 -E '^\s+iPhone' | sed -E 's/.*\(([0-9A-F-]{36})\).*/\1/')}
app=$here/bin/Debug/net10.0-ios/iossimulator-arm64/counter-ios.app

dotnet build "$here" -nologo -v:q
xcrun simctl boot "$device" 2>/dev/null || true
# The Simulator window, where Xcode has one; the simulator runs without it.
open -a Simulator 2>/dev/null || open -a "$(xcode-select -p)/Applications/Simulator.app" 2>/dev/null || true
xcrun simctl bootstatus "$device" -b >/dev/null
xcrun simctl install "$device" "$app"
exec xcrun simctl launch --console-pty --terminate-running-process "$device" com.navisurg.bibui.counter
