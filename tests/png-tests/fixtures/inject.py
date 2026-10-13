"""Splices tRNS, gAMA and iCCP chunks into fixtures generate.sh has just written.

The transparent grey/RGB value is the first pixel's own sample, so the key is
known to occur. Row 0's first pixel is stored unfiltered under every filter
(its left, up and up-left neighbours are all zero), so it can be read from the
inflated bytes without unfiltering anything.
"""
import struct
import zlib


def chunks(data):
    i = 8
    while i < len(data):
        n, t = struct.unpack('>I4s', data[i:i + 8])
        yield t, data[i + 8:i + 8 + n]
        i += 12 + n


def chunk(t, body):
    return struct.pack('>I', len(body)) + t + body + struct.pack('>I', zlib.crc32(t + body) & 0xffffffff)


def rewrite(path, make, before):
    """Insert the chunk make(ihdr, idat, plte) before the first chunk of type `before`."""
    data = open(path, 'rb').read()
    parts = list(chunks(data))
    ihdr = next(b for t, b in parts if t == b'IHDR')
    idat = zlib.decompress(b''.join(b for t, b in parts if t == b'IDAT'))
    plte = next((b for t, b in parts if t == b'PLTE'), b'')
    new, out = make(ihdr, idat, plte), [data[:8]]
    for t, b in parts:
        if t == before and new is not None:
            out.append(new)
            new = None
        out.append(chunk(t, b))
    open(path, 'wb').write(b''.join(out))


def first_pixel_key(ihdr, idat, plte):
    depth, ctype = ihdr[8], ihdr[9]
    size = (1 if ctype == 0 else 3) * depth // 8
    sample = idat[1:1 + size]
    if depth == 8:  # tRNS samples are always two bytes
        sample = b''.join(b'\x00' + bytes([v]) for v in sample)
    return chunk(b'tRNS', sample)


def palette_alpha(ihdr, idat, plte):
    entries = len(plte) // 3
    # Shorter than the palette on purpose: the rest stay opaque.
    return chunk(b'tRNS', bytes((i * 53) % 256 for i in range(max(1, entries - 2))))


def s15f16(v):
    return struct.pack('>i', round(v * 65536))


def xyz(x, y, z):
    return b'XYZ \0\0\0\0' + s15f16(x) + s15f16(y) + s15f16(z)


def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def inv(m):
    (a, b, c), (d, e, f), (g, h, i) = m
    det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
    return [[(e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det],
            [(f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det],
            [(d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det]]


def rgb_profile():
    """A minimal ICC v2 display profile, written here rather than copied from
    anywhere, so the fixture carries no third-party data. Its colorimetry is
    Adobe-RGB-compatible: primaries (0.64, 0.33), (0.21, 0.71), (0.15, 0.06),
    a D65 white, and a pure power curve of 563/256. The colorants are
    Bradford-adapted to the D50 profile connection space, as ICC v2 requires.
    The decoder skips the chunk; the test needs only a well-formed one there
    (LittleCMS, through Pillow's ImageCms, reads this one)."""
    prims = [(0.64, 0.33), (0.21, 0.71), (0.15, 0.06)]
    white = (0.95047, 1.0, 1.08883)  # D65
    d50 = (0.96420, 1.0, 0.82491)
    cols = [[x / y, 1.0, (1 - x - y) / y] for x, y in prims]
    m = [[cols[j][i] for j in range(3)] for i in range(3)]
    s = [sum(inv(m)[i][k] * white[k] for k in range(3)) for i in range(3)]
    rgb_to_xyz = [[m[i][j] * s[j] for j in range(3)] for i in range(3)]
    brad = [[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]]
    cs = [sum(brad[i][k] * white[k] for k in range(3)) for i in range(3)]
    cd = [sum(brad[i][k] * d50[k] for k in range(3)) for i in range(3)]
    scale = [[cd[i] / cs[i] if i == j else 0.0 for j in range(3)] for i in range(3)]
    adapted = mul(mul(mul(inv(brad), scale), brad), rgb_to_xyz)

    def desc(text):
        t = text.encode('ascii') + b'\0'
        return (b'desc\0\0\0\0' + struct.pack('>I', len(t)) + t
                + b'\0' * 4 + b'\0' * 4 + b'\0' * 2 + b'\0' + b'\0' * 67)

    curve = b'curv\0\0\0\0' + struct.pack('>IH', 1, 563)
    tags = [
        (b'desc', desc('bibui test RGB (Adobe RGB compatible)')),
        (b'cprt', b'text\0\0\0\0' + b'No copyright, use freely\0'),
        (b'wtpt', xyz(*d50)),
        (b'rXYZ', xyz(*(adapted[i][0] for i in range(3)))),
        (b'gXYZ', xyz(*(adapted[i][1] for i in range(3)))),
        (b'bXYZ', xyz(*(adapted[i][2] for i in range(3)))),
        (b'rTRC', curve), (b'gTRC', curve), (b'bTRC', curve),
    ]
    offset, table, body = 128 + 4 + 12 * len(tags), b'', b''
    shared = {}
    for sig, data in tags:
        if data in shared:  # the three TRCs share one curve
            table += struct.pack('>4sII', sig, shared[data], len(data))
            continue
        body += b'\0' * (-len(body) % 4)
        shared[data] = offset + len(body)
        table += struct.pack('>4sII', sig, offset + len(body), len(data))
        body += data
    size = offset + len(body)
    header = (struct.pack('>I', size) + b'\0' * 4 + bytes([2, 0x10, 0, 0])
              + b'mntrRGB XYZ ' + struct.pack('>6H', 2026, 1, 1, 0, 0, 0)
              + b'acsp' + b'\0' * 4 + struct.pack('>I', 0) + b'\0' * 4 + b'\0' * 4
              + b'\0' * 8 + struct.pack('>I', 0) + xyz(*d50)[8:] + b'\0' * 4
              + b'\0' * 16 + b'\0' * 28)
    assert len(header) == 128, len(header)
    return header + struct.pack('>I', len(tags)) + table + body


for name in ['gray8-trns', 'gray16-trns', 'rgb8-trns', 'rgb16-trns']:
    rewrite(f'{name}.png', first_pixel_key, b'IDAT')
for name in ['pal8-trns', 'pal2-trns-adam7']:
    rewrite(f'{name}.png', palette_alpha, b'IDAT')

rewrite('rgba8-gama.png', lambda *_: chunk(b'gAMA', struct.pack('>I', 70000)), b'IDAT')
rewrite('rgba8-iccp.png', lambda *_: chunk(b'iCCP', b'bibui-rgb\x00\x00' + zlib.compress(rgb_profile())), b'IDAT')
