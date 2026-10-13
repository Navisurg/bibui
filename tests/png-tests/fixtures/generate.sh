#!/usr/bin/env bash
# Regenerates the PNG decoder fixtures: every colour type and bit depth PNG
# defines, plain and Adam7-interlaced, with and without tRNS, plus gAMA/iCCP.
# Every PNG bibui itself writes is 8-bit RGBA from one encoder, so these are what
# keep the other paths honest. The test checks each against SkiaSharp's decode,
# so the generator does not have to be right, only varied.
#
#   tests/png-tests/fixtures/generate.sh     (needs ImageMagick 7)
set -euo pipefail
cd "$(dirname "$0")"
rm -f ./*.png

# 37x29: odd in both directions, so every Adam7 pass has a ragged edge and
# low-bit-depth rows end mid-byte. A gradient under noise exercises all five
# row filters; the alpha ramp gives translucent pixels.
src=$(mktemp --suffix=.png)
small=$(mktemp --suffix=.png)
trap 'rm -f "$src" "$small"' EXIT
magick -size 37x29 -seed 7 gradient:'#ff2010-#1040ff' \
  \( -size 37x29 xc: +noise Random -blur 0x0.6 \) -compose blend -define compose:args=35 -composite \
  \( -size 37x29 gradient:white-'rgba(0,0,0,0.15)' -rotate 90 -resize '37x29!' \) -alpha off -compose CopyOpacity -composite \
  -depth 16 "$src"
# A small-palette version for colour type 3 at 1, 2 and 4 bits.
magick "$src" -alpha off +dither -colors 4 "$small"

# No timestamps or text, so regenerating gives the same files.
strip="-define png:exclude-chunks=date,tIME,tEXt,zTXt,bKGD"

w() { # name, extra args...
  local name=$1; shift
  magick "$@" $strip "$name.png"
  magick "$@" -interlace PNG $strip "$name-adam7.png"
}

w gray1   "$src" -alpha off -colorspace gray -threshold 50% -depth 1 -define png:color-type=0 -define png:bit-depth=1
w gray2   "$src" -alpha off -colorspace gray -depth 2 -define png:color-type=0 -define png:bit-depth=2
w gray4   "$src" -alpha off -colorspace gray -depth 4 -define png:color-type=0 -define png:bit-depth=4
w gray8   "$src" -alpha off -colorspace gray -depth 8 -define png:color-type=0 -define png:bit-depth=8
w gray16  "$src" -alpha off -colorspace gray -depth 16 -define png:color-type=0 -define png:bit-depth=16
w rgb8    "$src" -alpha off -depth 8 -define png:color-type=2 -define png:bit-depth=8
w rgb16   "$src" -alpha off -depth 16 -define png:color-type=2 -define png:bit-depth=16
w pal1    "$small" -colors 2 -define png:color-type=3 -define png:bit-depth=1
w pal2    "$small" -define png:color-type=3 -define png:bit-depth=2
w pal4    "$src" -alpha off +dither -colors 16 -define png:color-type=3 -define png:bit-depth=4
w pal8    "$src" -alpha off +dither -colors 200 -define png:color-type=3 -define png:bit-depth=8
w graya8  "$src" -colorspace gray -depth 8 -define png:color-type=4 -define png:bit-depth=8
w graya16 "$src" -colorspace gray -depth 16 -define png:color-type=4 -define png:bit-depth=16
w rgba8   "$src" -depth 8 -define png:color-type=6 -define png:bit-depth=8
w rgba16  "$src" -depth 16 -define png:color-type=6 -define png:bit-depth=16

# tRNS and the colour-management chunks are spliced in afterwards (inject.py):
# ImageMagick picks its own colour type when there is alpha, and would not
# write a lone transparent grey or RGB value where the test wants one.
magick "$src" -alpha off -colorspace gray -depth 8 -define png:color-type=0 -define png:bit-depth=8 $strip gray8-trns.png
magick "$src" -alpha off -colorspace gray -depth 16 -define png:color-type=0 -define png:bit-depth=16 $strip gray16-trns.png
magick "$small" -define png:color-type=2 -define png:bit-depth=8 $strip rgb8-trns.png
magick "$small" -depth 16 -define png:color-type=2 -define png:bit-depth=16 $strip rgb16-trns.png
magick "$src" -alpha off +dither -colors 64 -define png:color-type=3 -define png:bit-depth=8 $strip pal8-trns.png
magick "$small" -define png:color-type=3 -define png:bit-depth=2 -interlace PNG $strip pal2-trns-adam7.png
magick "$src" -depth 8 -define png:color-type=6 $strip rgba8-gama.png
magick "$src" -depth 8 -define png:color-type=6 $strip rgba8-iccp.png
python3 inject.py

# Edges: one pixel, and one pixel wide with Adam7 (most passes empty).
magick -size 1x1 xc:'rgba(10,200,30,0.5)' -define png:color-type=6 $strip one-pixel.png
magick "$src" -crop 1x29+5+0 +repage -depth 8 -define png:color-type=6 -interlace PNG $strip one-column-adam7.png

ls -1 ./*.png | wc -l
