# bibui logo

"bibui" in rounded cells on a four-row grid: the b's teal, the i's and the u
ink, and the last i's dot teal.

| File | Use |
|---|---|
| `bibui-logo.svg` | On light backgrounds |
| `bibui-logo-on-dark.svg` | On dark backgrounds (ink becomes light grey) |
| `bibui-logo-animated.svg` | Animated, light: the cells fill in column by column, bottom row first, and the last dot lands last (~1.3 s, plays once, still under `prefers-reduced-motion`) |
| `bibui-logo-animated-on-dark.svg` | The same on dark backgrounds |

Colours: teal `#03A48C`, ink `#1E2024`, ink on dark `#E9EAEC`.

The SVGs are generated from one grid by `make-logo.py`. Change it there and
run `python3 docs/brand/make-logo.py`, rather than editing the SVGs by hand.
