"""Derive the infinity-with-arrow glyph as exact SVG path data.

The figure-eight is a Gerono lemniscate:  x = a cos t,  y = b sin t cos t.
Each span is converted from Hermite (point + tangent at both ends) to a cubic Bezier, which
is exact for the tangents and visually indistinguishable from the true curve at 16 segments.
Sampling it as a polyline would work too, but a curve that is actually curves stays crisp
when the icon is scaled to 256.
"""
import math

A = 78.0        # half-width of the figure eight
B = 60.0        # governs how fat the loops are
CX, CY = 128.0, 132.0

# Where the stroke stops and the arrow takes over. Just past the crossing on the way up the
# right loop, so the tail leaves heading north-east the way the reference does.
T_END = 1.68 * math.pi
GAP = 0.17 * math.pi          # the visible break the arrow flies out of
T_START = T_END - 2 * math.pi + GAP
SEGMENTS = 16


def point(t):
    return CX + A * math.cos(t), CY + B * math.sin(t) * math.cos(t)


def tangent(t):
    return -A * math.sin(t), B * math.cos(2 * t)


def fmt(v):
    return f"{v:.1f}".rstrip("0").rstrip(".")


def loop_path():
    """The figure eight, minus the gap, as one open path."""
    step = (T_END - T_START) / SEGMENTS
    x0, y0 = point(T_START)
    parts = [f"M{fmt(x0)} {fmt(y0)}"]

    for i in range(SEGMENTS):
        t0 = T_START + i * step
        t1 = t0 + step
        p0, p3 = point(t0), point(t1)
        d0, d1 = tangent(t0), tangent(t1)
        # Hermite -> Bezier: control points sit one third of the span along each tangent.
        c1 = (p0[0] + d0[0] * step / 3, p0[1] + d0[1] * step / 3)
        c2 = (p3[0] - d1[0] * step / 3, p3[1] - d1[1] * step / 3)
        parts.append(
            f"C{fmt(c1[0])} {fmt(c1[1])} {fmt(c2[0])} {fmt(c2[1])} {fmt(p3[0])} {fmt(p3[1])}")

    return " ".join(parts), point(T_END), tangent(T_END)


def arrow(end, tang, tip=(220.0, 48.0), barb=36.0):
    """The tail lifting to 45 degrees, and the head it ends in."""
    length = math.hypot(*tang)
    unit = (tang[0] / length, tang[1] / length)

    # Leave along the curve's own tangent, arrive at the tip travelling north-east, so the
    # join reads as one continuous stroke rather than a line glued to a curve.
    out = (math.cos(math.radians(-45)), math.sin(math.radians(-45)))
    c1 = (end[0] + unit[0] * 24, end[1] + unit[1] * 24)
    c2 = (tip[0] - out[0] * 30, tip[1] - out[1] * 30)
    tail = f"C{fmt(c1[0])} {fmt(c1[1])} {fmt(c2[0])} {fmt(c2[1])} {fmt(tip[0])} {fmt(tip[1])}"

    # Two barbs, 30 degrees either side of the direction of travel. A stroked chevron rather
    # than a filled triangle: it survives being small far better, and matches the reference's
    # open head.
    heads = []
    for spread in (30, -30):
        angle = math.radians(-45 + spread)
        heads.append((tip[0] - math.cos(angle) * barb, tip[1] - math.sin(angle) * barb))

    head = (f"M{fmt(heads[0][0])} {fmt(heads[0][1])} L{fmt(tip[0])} {fmt(tip[1])} "
            f"L{fmt(heads[1][0])} {fmt(heads[1][1])}")
    return tail, head


loop, end, tang = loop_path()
tail, head = arrow(end, tang)

print("LOOP + TAIL:")
print(loop + " " + tail)
print()
print("HEAD:")
print(head)
print()
print("end point", tuple(round(v, 1) for v in end))
