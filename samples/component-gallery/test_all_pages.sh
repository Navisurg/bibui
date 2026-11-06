#!/usr/bin/env bash
# Walks every gallery page through the gallery's test port, as a user would: for
# each sidebar group, open it, and for each of its entries, click it, check the
# page title says its name, and write a screenshot. The pages are read from the
# sidebar itself; bibui-port scrolls an entry below the sidebar's fold into view
# before it clicks it.
#
# Start the gallery with its test port on:
#
#     BIBUI_TEST_PORT=1 BIBUI_AGENT_CURSOR=off dotnet run --project samples/component-gallery
#
# BIBUI_AGENT_CURSOR=off draws no agent cursor and answers each step at once;
# without it, each click waits for the cursor to show it, for a person watching,
# and the walk takes minutes rather than seconds.
#
# then, in another shell:
#
#     samples/component-gallery/test_all_pages.sh [socket]
#
# With no socket, the running gallery's. bibui-port is the one on the PATH, or
# is published once from tools/bibui-port. Screenshots go to
# gallery-test-screenshots/ beside this script, or $GALLERY_SCREENSHOTS.
set -uo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
shots=${GALLERY_SCREENSHOTS:-$here/gallery-test-screenshots}
mkdir -p "$shots"

bin=$(command -v bibui-port || true)
if [ -z "$bin" ]; then
  bin=$repo/tools/bibui-port/bin/aot/bibui-port
  [ -x "$bin" ] || dotnet publish "$repo/tools/bibui-port" -c Release -o "$repo/tools/bibui-port/bin/aot" >/dev/null || exit 1
fi
if [ $# -gt 0 ]; then target=(--socket "$1"); else target=(--app component-gallery); fi
port() { "$bin" "${target[@]}" "$@"; }

# The ids of the boxes whose id starts with $1, in tree order.
ids() { port find "$1" 2>/dev/null | grep -o '^box #[^ ]*' | cut -c6- | grep "^$1"; }
# The open group's entries: nav-<route>, not the groups or the menu button.
entries() { ids nav- | grep -v -e '^nav-group-' -e '^nav-menu$'; }

port list >/dev/null || exit 1
printf '=== Component Gallery: every page ===\n  Screenshots: %s\n\n' "$shots"

# A window too narrow for the sidebar folds it behind the menu button, which
# shows it again; each click on a page folds it away.
unfold() {
  if port tree --id nav-menu --depth 0 >/dev/null 2>&1 && ! port tree --id sidebar-scroll --depth 0 >/dev/null 2>&1; then
    port click nav-menu >/dev/null
  fi
}

routes=0
failures=()
unfold
for group in $(ids nav-group-); do
  unfold
  # One group is open at a time, so its entries are the pages in the tree. With
  # none, the click closed it (it was open already), and a second opens it.
  port click "$group" >/dev/null
  pages=$(entries)
  if [ -z "$pages" ]; then port click "$group" >/dev/null; pages=$(entries); fi

  for entry in $pages; do
    route=${entry#nav-}
    routes=$((routes + 1))
    printf '  %-22s ' "$route"
    unfold
    if ! why=$(port click "$entry" 2>&1 >/dev/null); then
      echo "FAIL ($why)"; failures+=("$route: $why"); continue
    fi
    shown=$(port tree --id page-title | grep -o '"[^"]*"' | head -1 | tr -d '"')
    if [ "$shown" != "$route" ]; then
      echo "FAIL (the page shows '$shown')"; failures+=("$route: the page shows '$shown'"); continue
    fi
    if ! why=$(port screenshot "$shots/$route.png" 2>&1 >/dev/null); then
      echo "FAIL ($why)"; failures+=("$route: $why"); continue
    fi
    echo PASS
  done
done

printf '\n=== Results: %d passed, %d failed ===\n' $((routes - ${#failures[@]})) ${#failures[@]}
for f in "${failures[@]}"; do echo "  ✗ $f"; done
[ ${#failures[@]} -eq 0 ]
