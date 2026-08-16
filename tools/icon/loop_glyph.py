"""
Derive the loop-and-arrow mark as exact SVG path data.

The figure eight is a Gerono lemniscate,  x = a cos t,  y = b sin t cos t, converted span by
span from Hermite form (point and tangent at both ends) to cubic Beziers. That is exact for
the tangents, so the curve stays smooth at any size rather than showing the flat spots a
hand-placed control point leaves behind.

What makes it read as a drawn mark rather than a scribble is entirely in the breaks:

  * The crossing is woven. One strand runs through; the other stops short either side, so the
    eye reads one line passing behind another. Drawn as a continuous crossing it is a blob
    with four legs.
  * The arrow pierces the right loop. It begins inside the loop and ends outside it, so the
    loop is something the arrow comes *out of* rather than something it is glued to.
  * The loop is cut where the arrow passes. Without that gap the two shapes fuse at exactly
    the point the drawing is about.

The order of derivation matters and was got wrong first time. Choose where the arrow pierces
the loop, then derive the shaft from that point -- not the reverse. Positioning the shaft
first and solving for the intersection put it within a few degrees of the curve's own tangent
at the crossing, so the shaft and the loop ran side by side as a doubled line, which is the
blob this construction exists to avoid.
"""

import math

# Loop geometry, and b is the load-bearing number. The curve's slope where the strands cross
# is atan(b/a), and the shaft runs at 45 degrees: pick b near a and the two are within a few
# degrees of each other, so the loop and the arrow travel side by side as a doubled line --
# which is exactly the blob this construction exists to avoid. Taller loops push that slope
# past the shaft's, and the two separate cleanly. Flatter loops separate too, but a lemniscate
# flat enough to do it reads as a squashed pretzel.
A = 82.0
B = 86.0
CX, CY = 116.0, 132.0

# Where the arrow crosses the loop's upper right arc, as a value of t. Everything about the
# arrow follows from this point.
EXIT_T = 1.73 * math.pi

# How far the shaft runs each side of that crossing. Roughly half in, half out: the arrow has
# to be visibly inside the loop for piercing to read as piercing.
INSIDE = 46.0
OUTSIDE = 70.0
SHAFT_ANGLE = -45.0

# Stroke removed at each break, in radians of t.
WEAVE_GAP = 0.17
EXIT_GAP = 0.17

SEGMENTS_PER_RADIAN = 2.2


def point(t):
    return CX + A * math.cos(t), CY + B * math.sin(t) * math.cos(t)


def tangent(t):
    return -A * math.sin(t), B * math.cos(2 * t)


def fmt(v):
    return f"{v:.1f}".rstrip("0").rstrip(".")


def arc(t0, t1):
    """One span of the lemniscate as a chain of cubic Beziers."""
    count = max(3, int(abs(t1 - t0) * SEGMENTS_PER_RADIAN) + 1)
    step = (t1 - t0) / count
    x0, y0 = point(t0)
    parts = [f"M{fmt(x0)} {fmt(y0)}"]

    for i in range(count):
        a0 = t0 + i * step
        a1 = a0 + step
        p0, p3 = point(a0), point(a1)
        d0, d1 = tangent(a0), tangent(a1)
        c1 = (p0[0] + d0[0] * step / 3, p0[1] + d0[1] * step / 3)
        c2 = (p3[0] - d1[0] * step / 3, p3[1] - d1[1] * step / 3)
        parts.append(
            f"C{fmt(c1[0])} {fmt(c1[1])} {fmt(c2[0])} {fmt(c2[1])} {fmt(p3[0])} {fmt(p3[1])}")

    return " ".join(parts)


def build(barb=40.0, spread=26.0):
    direction = (math.cos(math.radians(SHAFT_ANGLE)), math.sin(math.radians(SHAFT_ANGLE)))
    pierce = point(EXIT_T)

    start = (pierce[0] - direction[0] * INSIDE, pierce[1] - direction[1] * INSIDE)
    tip = (pierce[0] + direction[0] * OUTSIDE, pierce[1] + direction[1] * OUTSIDE)

    # A hollow head, as in the reference. It stays sharp when the mark is scaled down, where a
    # filled triangle turns into a lump on the end of a line.
    corners = []
    for offset in (spread, -spread):
        angle = math.radians(SHAFT_ANGLE + offset)
        corners.append((tip[0] - math.cos(angle) * barb, tip[1] - math.sin(angle) * barb))

    # The shaft stops at the head's base rather than its tip, so it does not show through the
    # hollow middle.
    base = ((corners[0][0] + corners[1][0]) / 2, (corners[0][1] + corners[1][1]) / 2)

    return {
        # Left loop, through the woven crossing, up to just before the arrow.
        "loop_a": arc(math.pi / 2 + WEAVE_GAP, EXIT_T - EXIT_GAP),
        # Past the arrow, round the right and along the bottom, back to the crossing.
        "loop_b": arc(EXIT_T + EXIT_GAP, 2 * math.pi + math.pi / 2 - WEAVE_GAP),
        "shaft": f"M{fmt(start[0])} {fmt(start[1])} L{fmt(base[0])} {fmt(base[1])}",
        "head": (f"M{fmt(tip[0])} {fmt(tip[1])} L{fmt(corners[0][0])} {fmt(corners[0][1])} "
                 f"L{fmt(corners[1][0])} {fmt(corners[1][1])} Z"),
    }


def extent():
    """Bounding box of the mark, ignoring stroke, for placing it inside an icon."""
    xs, ys = [], []

    for i in range(721):
        x, y = point(i * math.pi / 360)
        xs.append(x)
        ys.append(y)

    # The arrow tip is the only part that reaches outside the lemniscate's own box.
    direction = (math.cos(math.radians(SHAFT_ANGLE)), math.sin(math.radians(SHAFT_ANGLE)))
    pierce = point(EXIT_T)
    xs.append(pierce[0] + direction[0] * OUTSIDE)
    ys.append(pierce[1] + direction[1] * OUTSIDE)

    return min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys)


if __name__ == "__main__":
    for name, d in build().items():
        print(f"{name}:\n{d}\n")
    print("extent (x, y, w, h):", tuple(round(v, 1) for v in extent()))
