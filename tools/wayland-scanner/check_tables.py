#!/usr/bin/env python3
"""Diff live wl_interface tables against the protocol XML they came from.

    dotnet run --project tests/wayland-tests -c Release -- --dump-tables /tmp/tables.txt
    python3 tools/wayland-scanner/check_tables.py /tmp/tables.txt [XML ...]

Independent of the C# generator on purpose: it re-derives from the XML exactly
what wayland-scanner puts in a *-protocol.c (name, version, counts, signature
strings with since-prefix and '?', the 3-slot untyped new_id, types) in the
format WlTables.Dump prints, and compares. The dump holds both the generated
tables and the core ones libwayland-client exports, so the latter are checked
against wayland.xml too: the generated core wrappers' opcodes depend on it.

It covers
every protocol the generator emits, and fails on an interface missing from the dump.
"""
import sys
import xml.etree.ElementTree as ET

XML = [
    "/usr/share/wayland/wayland.xml",
    "/usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml",
    "/usr/share/wayland-protocols/unstable/xdg-decoration/xdg-decoration-unstable-v1.xml",
    "/usr/share/wayland-protocols/staging/fractional-scale/fractional-scale-v1.xml",
    "/usr/share/wayland-protocols/stable/viewporter/viewporter.xml",
    "/usr/share/wayland-protocols/staging/cursor-shape/cursor-shape-v1.xml",
    "/usr/share/wayland-protocols/stable/presentation-time/presentation-time.xml",
]
CODE = {"int": "i", "uint": "u", "fixed": "f", "string": "s", "object": "o",
        "new_id": "n", "array": "a", "fd": "h"}


def message(m):
    since = int(m.get("since", "1"))
    sig, types = (str(since) if since > 1 else ""), []
    for a in m.findall("arg"):
        t = a.get("type")
        if a.get("allow-null") == "true":
            sig += "?"
        if t == "new_id" and not a.get("interface"):
            sig += "sun"
            types += ["-", "-", "-"]
            continue
        sig += CODE[t]
        types.append(a.get("interface") if t in ("object", "new_id") and a.get("interface") else "-")
    return f"{m.get('name')} '{sig}' [{','.join(types)}]"


def interfaces(paths):
    for path in paths:
        for i in ET.parse(path).getroot().iter("interface"):
            reqs, evs = i.findall("request"), i.findall("event")
            lines = [f"interface {i.get('name')} v{i.get('version')} methods={len(reqs)} events={len(evs)}"]
            lines += ["  r " + message(m) for m in reqs]
            lines += ["  e " + message(m) for m in evs]
            yield i.get("name"), lines


def main(dump_path, paths):
    expected = dict(interfaces(paths))
    mine, cur = {}, None
    for line in open(dump_path).read().splitlines():
        if line.startswith("interface "):
            cur = line.split()[1]
            mine[cur] = []
        mine[cur].append(line)
    bad = 0
    for name, lines in mine.items():
        if lines == expected.get(name):
            print(f"{name:32} matches XML ({len(lines) - 1} messages)")
            continue
        bad += 1
        print(f"{name:32} DIFFERS from XML")
        for line in lines:
            if line not in expected.get(name, []):
                print(f"   mine: {line}")
        for line in expected.get(name, []):
            if line not in lines:
                print(f"   xml:  {line}")
    missing = [n for n in expected if n not in mine]
    for n in missing:
        print(f"{n:32} in the XML but not in the dump")
    bad += len(missing)
    print("ok" if bad == 0 else f"FAILED: {bad} differ")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2:] or XML))
