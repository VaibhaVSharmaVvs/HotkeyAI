"""
Regenerate the logo candidates in assets/logo-candidates.

Throwaway once a winner is picked -- the winner's geometry moves into make_icon.py and this
goes away. It exists because the mark is four separate paths with breaks in them: pasted into
nine files by hand it would drift, and every tweak would be nine edits with nine chances to
get one wrong.

Usage:  python tools/icon/make_candidates.py [repo-root]
"""

import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))

import loop_glyph  # noqa: E402

MARK = loop_glyph.build()
GX, GY, GW, GH = loop_glyph.extent()


def mark(cx, cy, width, colour, stroke=13.0):
    """The loop and arrow, centred on (cx, cy) and scaled to `width`.

    `stroke` is in the glyph's own units, so it scales with the mark: a smaller placement
    keeps the same visual weight relative to the shape rather than turning into hairlines.
    """
    s = width / GW
    tx = cx - (GX + GW / 2) * s
    ty = cy - (GY + GH / 2) * s
    return (f'<g transform="translate({tx:.1f} {ty:.1f}) scale({s:.4f})" fill="none" '
            f'stroke="{colour}" stroke-width="{stroke:.1f}" stroke-linecap="round" '
            f'stroke-linejoin="round">'
            f'<path d="{MARK["loop_a"]}"/><path d="{MARK["loop_b"]}"/>'
            f'<path d="{MARK["shaft"]}"/><path d="{MARK["head"]}"/></g>')


def cut(cx, cy, width, stroke=17.0):
    """The mark as a hole: the same paths, stroked black inside a mask."""
    s = width / GW
    tx = cx - (GX + GW / 2) * s
    ty = cy - (GY + GH / 2) * s
    return (f'<g transform="translate({tx:.1f} {ty:.1f}) scale({s:.4f})" fill="none" '
            f'stroke="#000000" stroke-width="{stroke:.1f}" stroke-linecap="round" '
            f'stroke-linejoin="round">'
            f'<path d="{MARK["loop_a"]}"/><path d="{MARK["loop_b"]}"/>'
            f'<path d="{MARK["shaft"]}"/><path d="{MARK["head"]}"/></g>')


def spark(cx, cy, r, fill="#FFFFFF", opacity=None):
    op = f' opacity="{opacity}"' if opacity else ""
    k = r * 0.28
    return (f'<path d="M{cx} {cy - r} C{cx} {cy - k} {cx + k} {cy} {cx + r} {cy} '
            f'C{cx + k} {cy} {cx} {cy + k} {cx} {cy + r} '
            f'C{cx} {cy + k} {cx - k} {cy} {cx - r} {cy} '
            f'C{cx - k} {cy} {cx} {cy - k} {cx} {cy - r} Z" fill="{fill}"{op}/>')


def wrap(title, body):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" '
            f'width="256" height="256">\n  <title>{title}</title>\n{body}\n</svg>\n')


V = {}

V["01-keycap-elevated"] = wrap(
    "Keycap, elevated — the whole icon is one key, lit from above", """  <defs>
    <linearGradient id="skirt" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#3D6FE8"/><stop offset="1" stop-color="#1E3A8F"/>
    </linearGradient>
    <linearGradient id="face" x1="0.2" y1="0" x2="0.8" y2="1">
      <stop offset="0" stop-color="#8FC0FF"/><stop offset="0.55" stop-color="#5A9CF8"/>
      <stop offset="1" stop-color="#3B78E0"/>
    </linearGradient>
    <linearGradient id="gloss" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.5"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>
    <filter id="lift" x="-40%" y="-40%" width="180%" height="180%">
      <feDropShadow dx="0" dy="10" stdDeviation="10" flood-color="#0B1428" flood-opacity="0.45"/>
    </filter>
  </defs>
  <rect x="18" y="22" width="220" height="212" rx="46" fill="url(#skirt)" filter="url(#lift)"/>
  <rect x="36" y="38" width="184" height="166" rx="34" fill="url(#face)"/>
  <rect x="36" y="38" width="184" height="80" rx="34" fill="url(#gloss)"/>
""" + "  " + mark(128, 122, 150, "#0E1730", 15))

V["02-loop-disc"] = wrap(
    "Loop disc — one colour, one shape, nothing else", """  <defs>
    <linearGradient id="disc" x1="0.15" y1="0" x2="0.85" y2="1">
      <stop offset="0" stop-color="#6BA5FF"/><stop offset="1" stop-color="#2F5BE0"/>
    </linearGradient>
  </defs>
  <circle cx="128" cy="128" r="120" fill="url(#disc)"/>
""" + "  " + mark(128, 128, 178, "#FFFFFF", 15))

V["03-chord"] = wrap(
    "Chord — a modifier held, and the key that fires", """  <defs>
    <linearGradient id="c-bg" x1="0.1" y1="0" x2="0.9" y2="1">
      <stop offset="0" stop-color="#243050"/><stop offset="1" stop-color="#111624"/>
    </linearGradient>
    <linearGradient id="c-live" x1="0.2" y1="0" x2="0.8" y2="1">
      <stop offset="0" stop-color="#9CC6FF"/><stop offset="1" stop-color="#3B78E0"/>
    </linearGradient>
    <filter id="c-lift" x="-50%" y="-50%" width="200%" height="200%">
      <feDropShadow dx="-6" dy="10" stdDeviation="10" flood-color="#05070F" flood-opacity="0.6"/>
    </filter>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="58" fill="url(#c-bg)"/>
  <rect x="30" y="46" width="104" height="104" rx="28" fill="#46506E"/>
  <g filter="url(#c-lift)">
    <rect x="102" y="100" width="122" height="122" rx="34" fill="url(#c-live)"/>
  </g>
""" + "  " + mark(163, 161, 98, "#101A34", 20))

V["04-key-plan"] = wrap(
    "Key and plan — a key above the steps it runs", """  <defs>
    <linearGradient id="bg" x1="0.1" y1="0" x2="0.9" y2="1">
      <stop offset="0" stop-color="#5E86FF"/><stop offset="1" stop-color="#2B3EBF"/>
    </linearGradient>
    <linearGradient id="cap" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF"/><stop offset="1" stop-color="#D8E4FF"/>
    </linearGradient>
    <filter id="drop" x="-50%" y="-50%" width="200%" height="200%">
      <feDropShadow dx="0" dy="8" stdDeviation="9" flood-color="#101B45" flood-opacity="0.5"/>
    </filter>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="58" fill="url(#bg)"/>
  <g filter="url(#drop)">
    <rect x="54" y="26" width="148" height="112" rx="32" fill="url(#cap)"/>
  </g>
""" + "  " + mark(128, 82, 116, "#2B3EBF", 19) + """
  <rect x="52" y="168" width="152" height="16" rx="8" fill="#FFFFFF" opacity="0.95"/>
  <rect x="52" y="197" width="112" height="16" rx="8" fill="#FFFFFF" opacity="0.6"/>
  <rect x="52" y="226" width="72" height="16" rx="8" fill="#FFFFFF" opacity="0.35"/>""")


def knockout(name, title, stops, sheen=True):
    stop_xml = "".join(f'<stop offset="{o}" stop-color="{c}"/>' for o, c in stops)
    sheen_def = f"""
    <linearGradient id="{name}-sheen" x1="0" y1="0" x2="0.6" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.42"/>
      <stop offset="0.6" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>""" if sheen else ""

    body = f"""  <defs>
    <linearGradient id="{name}-body" x1="0.1" y1="0" x2="0.9" y2="1">{stop_xml}</linearGradient>{sheen_def}
    <mask id="{name}-cut">
      <rect width="256" height="256" fill="#FFFFFF"/>
      {cut(128, 128, 180, 18)}
    </mask>
  </defs>
  <rect x="12" y="12" width="232" height="232" rx="60" fill="url(#{name}-body)" mask="url(#{name}-cut)"/>"""

    if sheen:
        body += (f'\n  <rect x="12" y="12" width="232" height="232" rx="60" '
                 f'fill="url(#{name}-sheen)" mask="url(#{name}-cut)"/>')
    return wrap(title, body)


V["05-negative-loop"] = knockout(
    "n", "Negative loop — the mark cut clean through the key",
    [("0", "#7DB2FF"), ("0.5", "#4A82F0"), ("1", "#2440C8")])

V["09-negative-loop-violet"] = knockout(
    "v", "Negative loop, violet — the same shape, asking whether the blue should stay",
    [("0", "#A78BFF"), ("0.5", "#6D4AF0"), ("1", "#3A1FA8")])

V["06-key-spark"] = wrap(
    "Key and spark — the hotkey, and the AI that wrote what it does", """  <defs>
    <linearGradient id="k-bg" x1="0.1" y1="0" x2="0.9" y2="1">
      <stop offset="0" stop-color="#6E8BFF"/><stop offset="1" stop-color="#2B2FB8"/>
    </linearGradient>
    <linearGradient id="k-cap" x1="0.2" y1="0" x2="0.8" y2="1">
      <stop offset="0" stop-color="#FFFFFF"/><stop offset="1" stop-color="#CFDCFF"/>
    </linearGradient>
    <filter id="k-lift" x="-50%" y="-50%" width="200%" height="200%">
      <feDropShadow dx="0" dy="9" stdDeviation="10" flood-color="#0B1240" flood-opacity="0.55"/>
    </filter>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="58" fill="url(#k-bg)"/>
  <g filter="url(#k-lift)">
    <rect x="28" y="70" width="168" height="140" rx="38" fill="url(#k-cap)"/>
  </g>
""" + "  " + mark(112, 140, 134, "#2B2FB8", 17) + "\n  "
    + spark(204, 52, 32) + "\n  " + spark(236, 102, 16, opacity="0.85"))

V["07-loop-spark"] = wrap(
    "Loop and spark — flat and bold, with the AI half stated", f"""  <defs>
    <linearGradient id="s-bg" x1="0.1" y1="0" x2="0.9" y2="1">
      <stop offset="0" stop-color="#7C5CFF"/><stop offset="0.55" stop-color="#4A6BF5"/>
      <stop offset="1" stop-color="#22C9E0"/>
    </linearGradient>
    <mask id="s-cut">
      <rect width="256" height="256" fill="#FFFFFF"/>
      {cut(122, 136, 172, 18)}
      {spark(48, 54, 24, fill="#000000")}
    </mask>
  </defs>
  <rect x="12" y="12" width="232" height="232" rx="60" fill="url(#s-bg)" mask="url(#s-cut)"/>""")

V["08-slot-key"] = wrap(
    "Slot key — a key half-pressed into the machine", """  <defs>
    <linearGradient id="deck" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#3355D8"/><stop offset="1" stop-color="#1B2A9E"/>
    </linearGradient>
    <linearGradient id="cap8" x1="0.2" y1="0" x2="0.8" y2="1">
      <stop offset="0" stop-color="#A9CCFF"/><stop offset="1" stop-color="#5A9CF8"/>
    </linearGradient>
    <filter id="cast" x="-60%" y="-60%" width="220%" height="220%">
      <feDropShadow dx="0" dy="12" stdDeviation="12" flood-color="#0A1030" flood-opacity="0.55"/>
    </filter>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="58" fill="url(#deck)"/>
  <rect x="8" y="150" width="240" height="98" fill="#16226F" opacity="0.55"/>
  <g filter="url(#cast)">
    <rect x="42" y="38" width="172" height="130" rx="36" fill="url(#cap8)"/>
  </g>
""" + "  " + mark(128, 103, 138, "#12225E", 18) + """
  <rect x="44" y="194" width="168" height="16" rx="8" fill="#FFFFFF" opacity="0.9"/>""")


def main():
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    out = root / "assets" / "logo-candidates"
    out.mkdir(parents=True, exist_ok=True)

    for old in out.glob("*.svg"):
        old.unlink()

    for name, svg in sorted(V.items()):
        (out / f"{name}.svg").write_text(svg, encoding="utf-8")
        print("wrote", name)


if __name__ == "__main__":
    main()
