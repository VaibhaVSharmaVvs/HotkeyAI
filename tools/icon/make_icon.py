"""
Draw the Hotkey AI mark and write assets/logo.svg, assets/hotkeyai.ico and a PNG for docs.

The mark is a keycap seen slightly from above, with a loop-and-arrow on its face: the key is
what you press, the loop is an automation that runs and keeps running, and the arrow is it
getting somewhere. The curve itself comes from loop_glyph.py, which derives it rather than
eyeballing control points.

Generated rather than drawn in an editor, for the same reason docs/capabilities.md is
generated from the schema: the reviewable artefact should be the source. The .ico is committed
because MSBuild's ApplicationIcon needs a file on disk, and because the same file is embedded
for the tray icon -- one file, so the two cannot drift apart.

**Per-size hinting is the whole job here, not a nicety.** This mark is a thin stroke wrapped
around two holes with three deliberate breaks in it, and every one of those closes up as the
icon shrinks. Rendering one drawing at nine sizes gives a crisp 256 and a navy smudge at 16.
So each size band gets its own drawing: the stroke thickens, the breaks widen, the keycap
sheds its shadow and then its gloss and finally its skirt, and the mark grows to fill the room
that frees up. The silhouette and the glyph stay the same, so they still read as one icon.

Rasterising needs a real SVG engine -- gradients, masks, a mitred join on the arrowhead -- so
this shells out to headless Chrome. That is a design-time dependency only: the .ico is
committed, so building the app never needs it. Run this when the logo changes, not on build.

Usage:  python tools/icon/make_icon.py [repo-root]
"""

import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

from PIL import Image

sys.path.insert(0, str(pathlib.Path(__file__).parent))

import loop_glyph  # noqa: E402

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

CHROME = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    "/usr/bin/google-chrome",
    "/usr/bin/chromium",
]

# Rendered here and downsampled, which anti-aliases better than asking a browser for a 16px
# window and is not subject to any minimum window size.
RENDER_AT = 512

SKIRT_TOP, SKIRT_BOTTOM = "#3D6FE8", "#1E3A8F"
FACE_TOP, FACE_MID, FACE_BOTTOM = "#8FC0FF", "#5A9CF8", "#3B78E0"
INK = "#0E1730"


def spec(size):
    """How to draw the mark at this size.

    Each threshold is a specific thing giving up: the shadow first (it is blur, and blur is the
    first casualty), then the gloss (a soft white wash reads as a smear), then the skirt (the
    darker rim costs contrast it can no longer earn). What is left at 16px is a blue tile with
    the boldest possible glyph on it.
    """
    if size >= 64:
        return dict(shadow=True, gloss=True, skirt=True,
                    mark=134, stroke=15, weave=1.0, exit=1.0, hollow=True)
    if size >= 32:
        return dict(shadow=False, gloss=True, skirt=True,
                    mark=142, stroke=19, weave=1.3, exit=1.3, hollow=True)
    if size >= 24:
        return dict(shadow=False, gloss=False, skirt=True,
                    mark=154, stroke=25, weave=1.7, exit=1.7, hollow=False)
    return dict(shadow=False, gloss=False, skirt=False,
                mark=180, stroke=34, weave=2.2, exit=2.2, hollow=False)


def glyph(how):
    """The loop and arrow as SVG, at the gap widths this size needs."""
    weave, exit_gap = loop_glyph.WEAVE_GAP, loop_glyph.EXIT_GAP
    loop_glyph.WEAVE_GAP = weave * how["weave"]
    loop_glyph.EXIT_GAP = exit_gap * how["exit"]

    try:
        paths = loop_glyph.build()
        gx, gy, gw, gh = loop_glyph.extent()
    finally:
        loop_glyph.WEAVE_GAP, loop_glyph.EXIT_GAP = weave, exit_gap

    # Centred on the keycap's face, which sits a little above the middle of the tile.
    scale = how["mark"] / gw
    tx = 128 - (gx + gw / 2) * scale
    ty = (122 if how["skirt"] else 128) - (gy + gh / 2) * scale

    place = f'transform="translate({tx:.1f} {ty:.1f}) scale({scale:.4f})"'
    common = (f'fill="none" stroke="{INK}" stroke-width="{how["stroke"]}" '
              f'stroke-linecap="round"')

    # The head is separate so it can take a mitred join: a round join rounds off the apex,
    # which is the one part of an arrow that has to be a point. Below 24px it is filled
    # instead of hollow -- the hole in a hollow head is the first thing to disappear.
    head_style = (f'{common} stroke-linejoin="miter" stroke-miterlimit="12"'
                  if how["hollow"]
                  else f'fill="{INK}" stroke="{INK}" stroke-width="{how["stroke"] * 0.5:.1f}" '
                       f'stroke-linejoin="miter" stroke-miterlimit="12"')

    return (f'<g {place} {common} stroke-linejoin="round">'
            f'<path d="{paths["loop_a"]}"/><path d="{paths["loop_b"]}"/>'
            f'<path d="{paths["shaft"]}"/></g>'
            f'<g {place} {head_style}><path d="{paths["head"]}"/></g>')


def svg(how):
    shadow_def = """
    <filter id="lift" x="-40%" y="-40%" width="180%" height="180%">
      <feDropShadow dx="0" dy="10" stdDeviation="10" flood-color="#0B1428" flood-opacity="0.45"/>
    </filter>""" if how["shadow"] else ""

    if how["skirt"]:
        filt = ' filter="url(#lift)"' if how["shadow"] else ""
        body = (f'<rect x="18" y="22" width="220" height="212" rx="46" '
                f'fill="url(#skirt)"{filt}/>'
                f'<rect x="36" y="38" width="184" height="166" rx="34" fill="url(#face)"/>')
        if how["gloss"]:
            body += ('<rect x="36" y="38" width="184" height="80" rx="34" '
                     'fill="url(#gloss)"/>')
    else:
        # No room for a rim at this size, so the keycap becomes one confident tile.
        body = '<rect x="14" y="14" width="228" height="228" rx="52" fill="url(#face)"/>'

    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <title>Hotkey AI</title>
  <defs>
    <linearGradient id="skirt" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{SKIRT_TOP}"/><stop offset="1" stop-color="{SKIRT_BOTTOM}"/>
    </linearGradient>
    <linearGradient id="face" x1="0.2" y1="0" x2="0.8" y2="1">
      <stop offset="0" stop-color="{FACE_TOP}"/><stop offset="0.55" stop-color="{FACE_MID}"/>
      <stop offset="1" stop-color="{FACE_BOTTOM}"/>
    </linearGradient>
    <linearGradient id="gloss" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.5"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>{shadow_def}
  </defs>
  {body}
  {glyph(how)}
</svg>
"""


def chrome():
    for candidate in CHROME:
        if pathlib.Path(candidate).exists():
            return candidate

    found = shutil.which("chrome") or shutil.which("google-chrome") or shutil.which("chromium")

    if found:
        return found

    raise SystemExit(
        "Headless Chrome is needed to rasterise the SVG, and none was found.\n"
        "It is only needed when the logo changes -- assets/hotkeyai.ico is committed.")


def render(markup, size, browser, work):
    """One size, rendered large and downsampled."""
    page = work / f"{size}.html"
    shot = work / f"{size}.png"
    page.write_text(
        f'<!doctype html><meta charset="utf-8">'
        f'<style>html,body{{margin:0;padding:0;background:transparent}}'
        f'svg{{display:block;width:{RENDER_AT}px;height:{RENDER_AT}px}}</style>{markup}',
        encoding="utf-8")

    subprocess.run(
        [browser, "--headless", "--disable-gpu", "--hide-scrollbars",
         "--default-background-color=00000000",
         f"--window-size={RENDER_AT},{RENDER_AT}", f"--screenshot={shot}", str(page)],
        capture_output=True, check=False)

    if not shot.exists():
        raise SystemExit(f"Chrome produced no image for {size}px.")

    return Image.open(shot).convert("RGBA").resize((size, size), Image.LANCZOS)


def write_ico(path, images):
    """A multi-size .ico with PNG-compressed entries.

    PNG rather than DIB: supported since Vista, far less code than hand-rolling a 32bpp bitmap
    with its AND mask, and this app is Windows 11 only anyway.
    """
    from io import BytesIO

    blobs = []
    for image in images:
        buffer = BytesIO()
        image.save(buffer, format="PNG")
        blobs.append(buffer.getvalue())

    offset = 6 + 16 * len(blobs)
    out = bytearray(struct.pack("<HHH", 0, 1, len(blobs)))

    for image, blob in zip(images, blobs):
        side = 0 if image.width >= 256 else image.width   # 256 is stored as 0
        out += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    for blob in blobs:
        out += blob

    path.write_bytes(bytes(out))


def main():
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    assets = root / "assets"
    assets.mkdir(exist_ok=True)

    # The canonical drawing, unhinted: what the logo *is*, for docs and for anyone who wants
    # to put it somewhere this script does not know about.
    canonical = svg(spec(256))
    (assets / "logo.svg").write_text(canonical, encoding="utf-8")

    browser = chrome()

    with tempfile.TemporaryDirectory() as tmp:
        work = pathlib.Path(tmp)
        images = [render(svg(spec(size)), size, browser, work) for size in SIZES]

    ico = assets / "hotkeyai.ico"
    write_ico(ico, images)
    images[-1].save(assets / "hotkeyai.png")

    print(f"wrote {ico.relative_to(root)}  ({ico.stat().st_size:,} bytes)")
    print(f"      sizes {', '.join(str(s) for s in SIZES)}")
    print(f"wrote {(assets / 'logo.svg').relative_to(root)}")
    print(f"wrote {(assets / 'hotkeyai.png').relative_to(root)}")


if __name__ == "__main__":
    main()
