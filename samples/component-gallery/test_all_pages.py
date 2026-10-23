#!/usr/bin/env python3
"""Walks every gallery page through the gallery's test port, as a user would.

For each component: opens its sidebar group, clicks its entry, checks the page
title changed, and writes a screenshot. The pages are read from the sidebar
itself: each group's entries, once it is open. An entry is clicked at the
centre of its box in the UI tree (test.tree), with a move, a press and a
release (test.events); one below the sidebar's fold is scrolled into view
first.

Start the gallery with its test port on:

    BIBUI_TEST_PORT=1 dotnet run --project samples/component-gallery

then, in another shell:

    python3 samples/component-gallery/test_all_pages.py [socket]

The socket is $XDG_RUNTIME_DIR/bibui/component-gallery-<pid>.sock; with none
given, the newest of those. Screenshots go to gallery-test-screenshots/ beside
this script, or $GALLERY_SCREENSHOTS.
"""
import glob
import json
import os
import socket
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.environ.get("GALLERY_SCREENSHOTS") or os.path.join(HERE, "gallery-test-screenshots")


class Port:
    """One connection to the app's test port: a line out, a line in."""

    def __init__(self, path):
        self.sock = socket.socket(socket.AF_UNIX)
        self.sock.connect(path)
        self.io = self.sock.makefile("rwb")

    def ask(self, cmd):
        self.io.write((json.dumps(cmd) + "\n").encode())
        self.io.flush()
        answer = json.loads(self.io.readline())
        if not answer.get("ok"):
            raise RuntimeError(answer.get("error", "refused"))
        return answer.get("result")

    def tree(self):
        return self.ask({"$cmd": "test.tree"})

    def events(self, *events):
        self.ask({"$cmd": "test.events", "events": list(events)})

    def click_at(self, x, y):
        for kind in ("mouse_move", "mouse_down", "mouse_up"):
            self.events({"kind": kind, "x": x, "y": y, "button": "left"})


def find(node, id):
    """The node with this id in a tree, or None."""
    if node.get("id") == id:
        return node
    for child in node.get("children", []):
        if (found := find(child, id)) is not None:
            return found
    return None


def text_of(node):
    """The first text under a node."""
    if node.get("kind") == "text":
        return node.get("text")
    for child in node.get("children", []):
        if (text := text_of(child)) is not None:
            return text
    return None


def ids(node, prefix):
    """The ids under a node that start with prefix, in tree order."""
    found = [node["id"]] if node.get("id", "").startswith(prefix) else []
    for child in node.get("children", []):
        found += ids(child, prefix)
    return found


def pages(tree):
    """The open group's entries: nav-<route>, not the groups or the menu button."""
    return [e for e in ids(tree, "nav-") if not e.startswith("nav-group-") and e != "nav-menu"]


def centre(node):
    return node["x"] + node["w"] / 2, node["y"] + node["h"] / 2


def click_in_sidebar(port, id):
    """Clicks a sidebar entry, scrolling the list until its centre is in view.
    False when it is not in the tree: its group is closed."""
    for _ in range(60):
        tree = port.tree()
        entry, sidebar = find(tree, id), find(tree, "sidebar-scroll")
        if entry is None:
            return False
        x, y = centre(entry)
        if sidebar is None or sidebar["y"] <= y <= sidebar["y"] + sidebar["h"]:
            port.click_at(x, y)
            return True
        # Wheel steps, as a window reports them: negative moves the list's content up.
        sx, sy = centre(sidebar)
        port.events({"kind": "scroll", "x": sx, "y": sy, "dy": -3 if y > sidebar["y"] + sidebar["h"] else 3})
    return False


def main():
    paths = sys.argv[1:] or sorted(glob.glob(os.path.join(os.environ.get("XDG_RUNTIME_DIR", "/tmp"), "bibui", "component-gallery-*.sock")), key=os.path.getmtime)
    if not paths:
        sys.exit("No gallery test port: start the gallery with BIBUI_TEST_PORT=1 (see the top of this script).")
    port = Port(paths[-1])
    os.makedirs(SHOTS, exist_ok=True)
    print(f"=== Component Gallery: every page ===\n  Socket:      {paths[-1]}\n  Screenshots: {SHOTS}\n")

    failures, routes = [], 0
    for group in ids(port.tree(), "nav-group-"):
        # A window too narrow for the sidebar folds it behind the menu button.
        tree = port.tree()
        if find(tree, "nav-menu") is not None and find(tree, "sidebar-scroll") is None:
            port.click_at(*centre(find(tree, "nav-menu")))
        # Open the group, as a user would: one group is open at a time, so its
        # entries are the pages in the tree. With none, the click closed it (it
        # was open already), and a second opens it.
        click_in_sidebar(port, group)
        entries = pages(port.tree())
        if not entries:
            click_in_sidebar(port, group)
            entries = pages(port.tree())
        for entry in entries:
            route = entry[len("nav-"):]
            routes += 1
            print(f"  {route:<22} ", end="", flush=True)
            try:
                if not click_in_sidebar(port, entry):
                    raise RuntimeError("its entry is not in the sidebar")
                title = find(port.tree(), "page-title")
                if title is None or text_of(title) != route:
                    raise RuntimeError(f"the page shows {text_of(title) if title else None!r}")
                port.ask({"$cmd": "test.screenshot", "path": os.path.join(SHOTS, f"{route}.png")})
                print("PASS")
            except RuntimeError as e:
                print(f"FAIL ({e})")
                failures.append(f"{route}: {e}")

    print(f"\n=== Results: {routes - len(failures)} passed, {len(failures)} failed ===")
    for f in failures:
        print(f"  ✗ {f}")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
