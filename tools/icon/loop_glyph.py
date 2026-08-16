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

# The shaft's tail sits at the middle of the right loop, and the pierce point is then solved
# rather than chosen: the 45-degree ray from the tail is followed until it meets the curve.
# Anchoring the tail instead of the exit is what stops the arrow looking like it is grazing
# the loop's upper edge -- it starts at the loop's centre and leaves through its shoulder, so
# the loop is unmistakably something the arrow came out of.
TAIL = 0.50
OUTSIDE = 70.0
SHAFT_ANGLE = -45.0

# A sharp head, not a blunt one: a narrow spread over a long barb, which is the proportion a
# mouse pointer has. Drawn with a mitred join, because a round join literally rounds off the
# point -- the one part of the arrow that has to be sharp.
BARB = 54.0
SPREAD = 18.0

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


def direction():
    return math.cos(math.radians(SHAFT_ANGLE)), math.sin(math.radians(SHAFT_ANGLE))


def tail():
    """The shaft's inner end: the middle of the right loop."""
    return CX + TAIL * A, CY


def exit_parameter():
    """Where the ray from the tail leaves the loop, by bisection.

    The tail is well inside the loop and the ray leaves through the upper right arc, so the
    signed distance from the ray's line changes exactly once between the crossing and the
    loop's rightmost point.
    """
    d = direction()
    normal = (-d[1], d[0])
    origin = tail()

    def side(t):
        x, y = point(t)
        return (x - origin[0]) * normal[0] + (y - origin[1]) * normal[1]

    low, high = 1.5 * math.pi, 2 * math.pi

    if side(low) * side(high) > 0:
        raise ValueError("the shaft never leaves the loop; check TAIL or SHAFT_ANGLE")

    for _ in range(80):
        mid = (low + high) / 2
        if side(low) * side(mid) <= 0:
            high = mid
        else:
            low = mid

    return (low + high) / 2


def build():
    d = direction()
    t_exit = exit_parameter()
    pierce = point(t_exit)

    start = tail()
    tip = (pierce[0] + d[0] * OUTSIDE, pierce[1] + d[1] * OUTSIDE)

    # A hollow head, as in the reference. It stays sharp when the mark is scaled down, where a
    # filled triangle turns into a lump on the end of a line.
    corners = []
    for offset in (SPREAD, -SPREAD):
        angle = math.radians(SHAFT_ANGLE + offset)
        corners.append((tip[0] - math.cos(angle) * BARB, tip[1] - math.sin(angle) * BARB))

    # The shaft stops at the head's base rather than its tip, so it does not show through the
    # hollow middle.
    base = ((corners[0][0] + corners[1][0]) / 2, (corners[0][1] + corners[1][1]) / 2)

    return {
        # Left loop, through the woven crossing, up to just before the arrow.
        "loop_a": arc(math.pi / 2 + WEAVE_GAP, t_exit - EXIT_GAP),
        # Past the arrow, round the right and along the bottom, back to the crossing.
        "loop_b": arc(t_exit + EXIT_GAP, 2 * math.pi + math.pi / 2 - WEAVE_GAP),
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
    d = direction()
    pierce = point(exit_parameter())
    xs.append(pierce[0] + d[0] * OUTSIDE)
    ys.append(pierce[1] + d[1] * OUTSIDE)

    return min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys)


if __name__ == "__main__":
    for name, d in build().items():
        print(f"{name}:\n{d}\n")
    print("extent (x, y, w, h):", tuple(round(v, 1) for v in extent()))
