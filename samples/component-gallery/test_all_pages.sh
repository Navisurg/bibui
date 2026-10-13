#!/usr/bin/env bash
# Exercises every gallery page via the BibuiTestServer API on port 44334.
# For each component: opens its sidebar group the way a user would, clicks
# its nav entry, checks the page title changed, takes a screenshot, and
# reports pass/fail. No shortcuts: an entry in a closed group is 0px tall
# and cannot be clicked, so the group is opened first.
#
# The gallery opens no port unless asked to. Start it with the test server on,
# writing its screenshots here:
#
#   BIBUI_TEST_SERVER=1 \
#   BIBUI_TEST_SERVER_OUT=samples/component-gallery/gallery-test-screenshots \
#     dotnet run --project samples/component-gallery
#
# Every request carries the server's token, read from the file the server
# writes when it starts ($XDG_RUNTIME_DIR/bibui-test-server-PORT.token), or
# from BIBUI_TEST_SERVER_TOKEN. Screenshot paths are relative to the server's
# output directory (BIBUI_TEST_SERVER_OUT).

set -euo pipefail

HOST=127.0.0.1
PORT=${BIBUI_TEST_SERVER_PORT:-44334}
SCREENSHOT_DIR="${BIBUI_TEST_SERVER_OUT:-$(cd "$(dirname "$0")" && pwd)/gallery-test-screenshots}"
TOKEN_FILE="${XDG_RUNTIME_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/bibui}/bibui-test-server-$PORT.token"

if [ -n "${BIBUI_TEST_SERVER_TOKEN:-}" ]; then
    TOKEN="$BIBUI_TEST_SERVER_TOKEN"
elif [ -r "$TOKEN_FILE" ]; then
    TOKEN="$(cat "$TOKEN_FILE")"
else
    echo "ERROR: no test server token at $TOKEN_FILE"
    echo "  Start the gallery with BIBUI_TEST_SERVER=1 (see the top of this script)."
    exit 1
fi

PASS=0
FAIL=0
ERRORS=()

# One request: the command's JSON object with the token added as its first member.
send() {
    printf '{"token":"%s",%s\n' "$TOKEN" "${1#\{}" | nc -w2 "$HOST" "$PORT" 2>/dev/null
}

check_ok() {
    # Strip leading UTF-8 BOM that .NET's StreamWriter emits, then parse JSON
    printf '%s' "$1" | python3 -c "
import sys, json
raw = sys.stdin.buffer.read().decode('utf-8-sig').strip()
try:
    d = json.loads(raw)
    sys.exit(0 if d.get('ok') else 1)
except Exception:
    sys.exit(1)
" 2>/dev/null
}

# Group slug (nav-group-<slug>) and the routes under it, in sidebar order:
# the same as NavGroups() in Program.cs.
NAV_GROUPS=(
    "inputs-and-forms:Button Input Textarea TextField Checkbox Switch RadioGroup Select Slider Toggle ToggleGroup DatePicker Calendar TimePicker ColorPicker"
    "display:Text Badge Avatar Card Alert Separator Label Progress Skeleton Spinner RatioBox KeyboardShortcut Table Markdown PagedTable FormField ListItem EmptyState Toast"
    "overlays:Dialog AlertDialog Popover HoverPreview Tooltip Drawer DropdownMenu ContextMenu CommandPalette"
    "navigation:Tabs Accordion Expander NavBar Sidebar Breadcrumb Pagination MenuBar"
    "layout:SplitPane Carousel ButtonStrip Stack InteractiveBox Section Scrollbar"
)
ALL_ROUTES=()
for g in "${NAV_GROUPS[@]}"; do ALL_ROUTES+=(${g#*:}); done

# Whether a command answered ok with a result of true.
is_true() {
    printf '%s' "$1" | python3 -c "
import sys, json
raw = sys.stdin.buffer.read().decode('utf-8-sig').strip()
try:
    d = json.loads(raw)
    sys.exit(0 if d.get('ok') and d.get('result') is True else 1)
except Exception:
    sys.exit(1)
" 2>/dev/null
}

# Whether the page shows <route> as its title: the text of the content
# panel's page-title (the sidebar has an entry of the same name).
page_is() {
    send '{"cmd":"get_text","id":"page-title"}' | python3 -c "
import sys, json
raw = sys.stdin.buffer.read().decode('utf-8-sig').strip()
try:
    sys.exit(0 if json.loads(raw).get('result') == sys.argv[1] else 1)
except Exception:
    sys.exit(1)
" "$1" 2>/dev/null
}

# In a window too narrow for the sidebar, it is folded behind the menu
# button (nav-menu) and shown in place of the page: open it, as a user would,
# when its list is not there. Picking a page closes it again.
open_menu() {
    if is_true "$(send '{"cmd":"is_visible","id":"nav-menu"}')" \
        && ! is_true "$(send '{"cmd":"is_visible","id":"sidebar-scroll"}')"; then
        send '{"cmd":"click","id":"nav-menu"}' >/dev/null
    fi
}

# Clicks <id> in the sidebar's list until <check> (shell text) holds: from the
# top of the list, then a step further down each time. A short window shows
# only part of the list, and an entry below its fold cannot be clicked until
# it is scrolled into view, as for a user.
click_in_list() {
    send '{"cmd":"scroll","x":40,"y":200,"dy":-10000}' >/dev/null
    for _ in $(seq 1 16); do
        send "{\"cmd\":\"click\",\"id\":\"$1\"}" >/dev/null
        if eval "$2"; then return 0; fi
        send '{"cmd":"scroll","x":40,"y":200,"dy":120}' >/dev/null
    done
    return 1
}

echo "=== Component Gallery — full page test ==="
echo "  Port:        $PORT"
echo "  Screenshots: $SCREENSHOT_DIR"
echo "  Routes:      ${#ALL_ROUTES[@]}"
echo ""

# Verify server is reachable
PING=$(send '{"cmd":"ping"}')
if ! check_ok "$PING"; then
    echo "ERROR: Cannot reach test server on $HOST:$PORT"
    echo "  Make sure the gallery is running with BIBUI_TEST_SERVER=1, and the token is current."
    exit 1
fi
echo "  Server ping: OK"
echo ""

for g in "${NAV_GROUPS[@]}"; do
  group="${g%%:*}"
  for route in ${g#*:}; do
    printf "  %-22s " "$route"

    # Open the route's group first, as a user would. Clicking the open
    # group's header would close it, so it is clicked only when its entries
    # are not there: whichever group a previous run left open.
    open_menu
    first="${g#*:}"; first="${first%% *}"
    if ! is_true "$(send "{\"cmd\":\"is_visible\",\"id\":\"nav-${first}\"}")"; then
        click_in_list "nav-group-${group}" "is_true \"\$(send '{\"cmd\":\"is_visible\",\"id\":\"nav-${first}\"}')\"" || true
    fi

    # Navigate to the page, then check it is the one shown: the server
    # answers ok to any click it accepts, whether or not anything happened.
    if ! click_in_list "nav-${route}" "page_is \"${route}\""; then
        printf "FAIL (nav)\n"
        FAIL=$((FAIL+1))
        ERRORS+=("$route: nav click did not open the page")
        continue
    fi

    # Take screenshot
    SHOT=$(send "{\"cmd\":\"screenshot\",\"path\":\"${route}.png\"}")
    if ! check_ok "$SHOT"; then
        printf "FAIL (screenshot)\n"
        FAIL=$((FAIL+1))
        ERRORS+=("$route: screenshot failed — $SHOT")
        continue
    fi

    printf "PASS\n"
    PASS=$((PASS+1))
  done
done

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="

if [ ${#ERRORS[@]} -gt 0 ]; then
    echo ""
    echo "Failures:"
    for e in "${ERRORS[@]}"; do
        echo "  ✗ $e"
    done
    exit 1
fi
