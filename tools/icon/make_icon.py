"""
Draw the Hotkey AI mark and write assets/hotkeyai.ico plus a PNG for docs.

The icon is generated rather than hand-drawn in an editor, for the same reason
docs/capabilities.md is generated from the schema: the reviewable artefact should be the
source, not the output. A .ico still has to exist on disk because MSBuild's ApplicationIcon
takes a file, so the binary is committed -- but it is a build product of this script, and
changing the mark means editing code that shows up in a diff.

The mark: a keycap with a lightning bolt. The keycap is the identity -- this is a hotkey
manager, and a keycap says so where a bare bolt would only say "automation, probably". The
bolt is what the keypress does. Together they read as the product in one glance: press a key,
something happens.

Two variants, chosen per size, which is ordinary icon hinting rather than a hack. Below 24px
the keycap's skirt is a smudge of dark blue that costs contrast and returns nothing, so small
sizes drop it and grow the bolt instead. The silhouette and the bolt stay the same, so the
tray icon and the taskbar icon still look like each other.

Usage:  python tools/icon/make_icon.py [repo-root]
"""

import struct
import sys
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw

# Supersample, then downsample. PIL has no antialiased polygon fill, and the bolt's diagonals
# look like a staircase without this.
SS = 8

# Palette.Accent, and a blue dark enough to read as the shadowed side of a key.
FACE = (0x5A, 0x9C, 0xF8)
SKIRT = (0x2F, 0x5F, 0xAA)
INK = (0x14, 0x16, 0x1C)

# Every size Windows asks for: 16 tray, 24/32 taskbar and alt-tab, 48 Explorer large,
# 256 the extra-large view and the installer.
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

# Below this, draw the simplified form.
DETAIL_FROM = 24


def bolt(cx, cy, height, width):
    """A lightning bolt centred on (cx, cy).

    The two horizontal bars are the first thing to disappear when this is scaled down, so
    they are deliberately deep: 0.07 and 0.11 of the height either side of centre.
    """
    return [
        (cx + 0.18 * width, cy - 0.50 * height),
        (cx - 0.50 * width, cy + 0.07 * height),
        (cx - 0.06 * width, cy + 0.07 * height),
        (cx - 0.24 * width, cy + 0.50 * height),
        (cx + 0.50 * width, cy - 0.11 * height),
        (cx + 0.06 * width, cy - 0.11 * height),
    ]


def render(size):
    """One icon, at one size, on a 256-unit design grid."""
    canvas = size * SS
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    u = canvas / 256.0

    if size >= DETAIL_FROM:
        # Skirt first, then a face inset further at the bottom than the top. That asymmetry
        # is the whole trick: it is what makes a rounded square read as a key rather than as
        # a generic app tile.
        draw.rounded_rectangle([22 * u, 24 * u, 234 * u, 232 * u], radius=40 * u, fill=SKIRT)
        draw.rounded_rectangle([40 * u, 40 * u, 216 * u, 202 * u], radius=28 * u, fill=FACE)
        draw.polygon(bolt(128 * u, 121 * u, 138 * u, 88 * u), fill=INK)
    else:
        draw.rounded_rectangle([20 * u, 20 * u, 236 * u, 236 * u], radius=48 * u, fill=FACE)
        draw.polygon(bolt(128 * u, 128 * u, 168 * u, 104 * u), fill=INK)

    return image.resize((size, size), Image.LANCZOS)


def write_ico(path, images):
    """Write a multi-size .ico with PNG-compressed entries.

    PNG entries rather than DIB: supported since Vista, far less code than hand-rolling a
    32bpp bitmap with its AND mask, and this app is Windows 11 only anyway.
    """
    encoded = []

    for image in images:
        buffer = BytesIO()
        image.save(buffer, format="PNG")
        encoded.append(buffer.getvalue())

    # ICONDIR, then one 16-byte ICONDIRENTRY each, then the images.
    offset = 6 + 16 * len(encoded)
    out = bytearray(struct.pack("<HHH", 0, 1, len(encoded)))

    for image, blob in zip(images, encoded):
        # 256 is stored as 0; the field is a single byte.
        side = 0 if image.width >= 256 else image.width
        out += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    for blob in encoded:
        out += blob

    path.write_bytes(bytes(out))


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    assets = root / "assets"
    assets.mkdir(exist_ok=True)

    images = [render(size) for size in SIZES]

    ico = assets / "hotkeyai.ico"
    write_ico(ico, images)

    png = assets / "hotkeyai.png"
    images[-1].save(png)

    print(f"wrote {ico.relative_to(root)}  ({ico.stat().st_size:,} bytes, "
          f"{len(SIZES)} sizes: {', '.join(str(s) for s in SIZES)})")
    print(f"wrote {png.relative_to(root)}")


if __name__ == "__main__":
    main()
