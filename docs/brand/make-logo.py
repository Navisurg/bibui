#!/usr/bin/env python3
"""Generates bibui's logo SVGs from one grid, so every variant agrees.

    python3 docs/brand/make-logo.py

The mark is "bibui" in rounded cells on a four-row grid: the b's are teal,
the i's and the u ink, and the last i's dot teal.
"""
import os

CELL_W, CELL_H = 106, 100   # a cell, a little wider than tall
GAP = 14                    # between cells inside a letter
LETTER = 44                 # between letters
U_GAP = 50                  # between the u's two stems
RADIUS = 14

TEAL, INK, INK_ON_DARK = "#03A48C", "#1E2024", "#E9EAEC"

def col(colour, *rows, **others):
    """A column: row -> colour, for rows 0 (top) to 3."""
    c = {r: colour for r in rows}
    c.update({int(k[1:]): v for k, v in others.items()})
    return c

# Each letter: its columns, and the gap between them. The u's bowl is a bar
# under both stems, added in cells().
LETTERS = [
    ("b", [col("teal", 0, 1, 2, 3), col("teal", 1, 2, 3)], GAP),
    ("i", [col("ink", 0, 1, 2, 3)], GAP),
    ("b", [col("teal", 0, 1, 2, 3), col("teal", 1, 2, 3)], GAP),
    ("u", [col("ink", 1, 2), col("ink", 1, 2)], U_GAP),
    ("i", [col("ink", 1, 2, 3, r0="teal")], GAP),
]

def cells():
    """(x, y, w, h, colour, column index, row) for every shape, left to right."""
    out, x, col = [], 0, 0  # col: running column index, for the animation
    for name, columns, inner in LETTERS:
        gap = inner
        x0 = x
        for c, cells_ in enumerate(columns):
            cx = x + c * (CELL_W + gap)
            for r, colour in sorted(cells_.items()):
                out.append((cx, r * (CELL_H + GAP), CELL_W, CELL_H, colour, col, r))
            col += 1
        width = len(columns) * CELL_W + (len(columns) - 1) * gap
        if name == "u":   # the bowl: one bar under both stems
            out.append((x0, 3 * (CELL_H + GAP), width, CELL_H, "ink", col - 1, 3))
        x += width + LETTER
    return out, x - LETTER, 4 * CELL_H + 3 * GAP

def svg(ink, title, animated=False, pad=0):
    shapes, w, h = cells()
    W, H = w + 2 * pad, h + 2 * pad
    fill = {"teal": TEAL, "ink": ink}
    lines = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="bibui">',
             f'  <title>{title}</title>']
    if animated:
        # Cells drop into place column by column, bottom row first, like a
        # level meter filling; then the two teal dots land last with a bounce.
        lines += ['  <style>',
                  '    .c { transform-box: fill-box; transform-origin: 50% 100%; opacity: 0;',
                  '         animation: drop 520ms cubic-bezier(.2,.9,.25,1.15) forwards; }',
                  '    @keyframes drop { 0% { opacity: 0; transform: translateY(-60%) scale(.6); }',
                  '                      60% { opacity: 1; }',
                  '                      100% { opacity: 1; transform: none; } }',
                  '    @media (prefers-reduced-motion: reduce) { .c { animation: none; opacity: 1; } }',
                  '  </style>']
    lines.append(f'  <g transform="translate({pad} {pad})">')
    last_col = max(s[5] for s in shapes)
    for (x, y, cw, ch, colour, col, r) in shapes:
        attrs = f'x="{x}" y="{y}" width="{cw}" height="{ch}" rx="{RADIUS}" fill="{fill[colour]}"'
        if animated:
            delay = col * 70 + (3 - r) * 55
            if r == 0 and col == last_col:
                delay += 260   # the last i's dot lands last
            attrs = f'class="c" style="animation-delay:{delay}ms" ' + attrs
        lines.append(f'    <rect {attrs}/>')
    lines += ['  </g>', '</svg>', '']
    return "\n".join(lines)

here = os.path.dirname(os.path.abspath(__file__))
for name, ink, animated in [("bibui-logo.svg", INK, False),
                            ("bibui-logo-on-dark.svg", INK_ON_DARK, False),
                            ("bibui-logo-animated.svg", INK, True),
                            ("bibui-logo-animated-on-dark.svg", INK_ON_DARK, True)]:
    with open(os.path.join(here, name), "w") as f:
        f.write(svg(ink, "bibui", animated))
print(cells()[1:], len(cells()[0]), "shapes")
