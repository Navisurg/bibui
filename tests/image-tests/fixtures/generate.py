#!/usr/bin/env python3
"""Regenerates the JPEG and WebP decoder fixtures (needs Pillow with WebP).

The pictures are flat blocks of known colour, so a lossy decode can be checked
at block centres within a tolerance, plus one lossless WebP whose every pixel is
known exactly. The test also checks each against SkiaSharp's decode, so the
generator does not have to be right, only varied.

    tests/image-tests/fixtures/generate.py
"""
import os
from PIL import Image, ImageOps

os.chdir(os.path.dirname(os.path.abspath(__file__)))
for name in os.listdir("."):
    if name.endswith((".jpg", ".webp")):
        os.remove(name)

RED, GREEN, BLUE, YELLOW = (230, 30, 30), (30, 200, 60), (40, 60, 220), (240, 220, 40)


def quadrants(w=24, h=16):
    """Upright: red top left, green top right, blue bottom left, yellow bottom right."""
    im = Image.new("RGB", (w, h))
    for y in range(h):
        for x in range(w):
            im.putpixel((x, y), (RED, GREEN, BLUE, YELLOW)[(y >= h // 2) * 2 + (x >= w // 2)])
    return im


def cmyk(im):
    """With the black in K, as a print workflow separates it; Pillow's own
    convert("CMYK") leaves K at 0, which would not tell inverted from not."""
    out = Image.new("CMYK", im.size)
    for y in range(im.height):
        for x in range(im.width):
            r, g, b = im.getpixel((x, y))
            k = 255 - max(r, g, b)
            ink = lambda v: round((255 - v - k) * 255 / (255 - k))
            out.putpixel((x, y), (ink(r), ink(g), ink(b), k))
    return out


upright = quadrants()

# JPEG: 4:4:4 baseline, 4:2:0 progressive, grey, CMYK (Adobe, inverted, as
# Photoshop writes it), and the upright picture stored under each EXIF
# orientation, so every one of them decodes to the same thing.
upright.save("quadrants-444.jpg", quality=95, subsampling=0)
upright.save("quadrants-420-progressive.jpg", quality=90, subsampling=2, progressive=True)
upright.convert("L").save("quadrants-grey.jpg", quality=95)
cmyk(upright).save("quadrants-cmyk.jpg", quality=95, subsampling=0)

# What turns the upright picture into the stored one: the inverse of the
# transform each orientation asks a viewer to apply.
store = {
    2: Image.Transpose.FLIP_LEFT_RIGHT,
    3: Image.Transpose.ROTATE_180,
    4: Image.Transpose.FLIP_TOP_BOTTOM,
    5: Image.Transpose.TRANSPOSE,
    6: Image.Transpose.ROTATE_90,   # a viewer turns it 90° clockwise
    7: Image.Transpose.TRANSVERSE,
    8: Image.Transpose.ROTATE_270,  # a viewer turns it 90° anticlockwise
}
for orientation in range(1, 9):
    stored = upright if orientation == 1 else upright.transpose(store[orientation])
    exif = Image.Exif()
    exif[0x0112] = orientation
    stored.save(f"exif-orientation-{orientation}.jpg", quality=95, subsampling=0, exif=exif.tobytes())
    # Pillow agrees on what the stored picture means.
    assert ImageOps.exif_transpose(Image.open(f"exif-orientation-{orientation}.jpg")).size == upright.size

# WebP: lossy, lossy with alpha, lossless with alpha, and an animation.
upright.save("quadrants-lossy.webp", quality=90)
translucent = upright.convert("RGBA")
for y in range(translucent.height):
    for x in range(translucent.width):
        r, g, b, _ = translucent.getpixel((x, y))
        translucent.putpixel((x, y), (r, g, b, 255 if x < translucent.width // 2 else 96))
translucent.save("quadrants-lossy-alpha.webp", quality=90)

# Every pixel distinct, alpha from 0 to 255, so a lossless decode is checked whole.
exact = Image.new("RGBA", (7, 5))
for y in range(5):
    for x in range(7):
        exact.putpixel((x, y), (x * 40, y * 60, (x * 7 + y * 13) % 256, (x + y * 7) * 255 // 34))
exact.save("exact-lossless.webp", lossless=True, exact=True)

frames = [quadrants(), quadrants().transpose(Image.Transpose.ROTATE_180)]
frames[0].save("animated.webp", save_all=True, append_images=frames[1:], duration=100, loop=0, lossless=True)
