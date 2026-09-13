"""
barchart.py — grouped-bar SVG in the Signal house style.

Importable: build_bars(groups, series, title, out, ylabel, invert) where
`groups` are the x categories, `series` maps a legend label -> list of values
(one per group). Used to draw the shared-vs-independent-vs-fixed comparison as
demand rises. Kept dependency-free like plot.py.
"""
from __future__ import annotations

VOID, BORDER, TMUTED, TBRIGHT = "#111009", "#3D3A30", "#6A6358", "#EDE8DC"
COLORS = ["#E8C068", "#6B7A8D", "#7D9A6A", "#C47A5A", "#9A7AB0"]
W, H = 860, 460
PAD_L, PAD_R, PAD_T, PAD_B = 66, 20, 60, 62


def build_bars(groups, series, out, title="Signal", ylabel="value"):
    labels = list(series.keys())
    allv = [v for vs in series.values() for v in vs]
    lo, hi = min(allv + [0]), max(allv + [0])
    span = (hi - lo) or 1
    lo -= span * 0.05; hi += span * 0.08
    span = hi - lo

    plot_w = W - PAD_L - PAD_R
    plot_h = H - PAD_T - PAD_B
    ng, ns = len(groups), len(labels)
    gw = plot_w / ng
    bw = gw * 0.72 / ns

    def y(v): return PAD_T + (hi - v) / span * plot_h

    s = [f'<svg viewBox="0 0 {W} {H}" xmlns="http://www.w3.org/2000/svg" '
         f'font-family="JetBrains Mono,monospace">',
         f'<rect width="{W}" height="{H}" fill="{VOID}"/>',
         f'<text x="{PAD_L}" y="32" fill="{TBRIGHT}" font-family="Rajdhani,sans-serif" '
         f'font-weight="700" font-size="19" letter-spacing="1">{title}</text>']

    for i in range(5):
        gv = lo + span * i / 4
        yy = y(gv)
        s.append(f'<line x1="{PAD_L}" y1="{yy:.1f}" x2="{W-PAD_R}" y2="{yy:.1f}" '
                 f'stroke="{BORDER}" stroke-width="0.6"/>')
        s.append(f'<text x="{PAD_L-8}" y="{yy+4:.1f}" fill="{TMUTED}" font-size="11" '
                 f'text-anchor="end">{gv:.0f}</text>')
    y0 = y(0)
    s.append(f'<line x1="{PAD_L}" y1="{y0:.1f}" x2="{W-PAD_R}" y2="{y0:.1f}" '
             f'stroke="{TMUTED}" stroke-width="1.2"/>')
    s.append(f'<text x="18" y="{H/2:.0f}" fill="{TMUTED}" font-size="12" '
             f'text-anchor="middle" transform="rotate(-90 18 {H/2:.0f})">{ylabel}</text>')

    for gi, g in enumerate(groups):
        gx = PAD_L + gi * gw
        for si, lab in enumerate(labels):
            v = series[lab][gi]
            bx = gx + gw * 0.14 + si * bw
            yv = y(v)
            top, hgt = (min(yv, y0), abs(yv - y0))
            s.append(f'<rect x="{bx:.1f}" y="{top:.1f}" width="{bw*0.88:.1f}" '
                     f'height="{hgt:.1f}" fill="{COLORS[si%len(COLORS)]}" opacity="0.9"/>')
            s.append(f'<text x="{bx+bw*0.44:.1f}" y="{top-4:.1f}" fill="{TBRIGHT}" '
                     f'font-size="10" text-anchor="middle">{v:.0f}</text>')
        s.append(f'<text x="{gx+gw/2:.1f}" y="{H-PAD_B+22:.0f}" fill="{TMUTED}" '
                 f'font-size="12" text-anchor="middle">{g}</text>')

    for si, lab in enumerate(labels):
        lx = PAD_L + si * 175
        s.append(f'<rect x="{lx}" y="{H-24}" width="12" height="12" '
                 f'fill="{COLORS[si%len(COLORS)]}"/>')
        s.append(f'<text x="{lx+18}" y="{H-14}" fill="{TBRIGHT}" font-size="12">{lab}</text>')
    s.append("</svg>")
    open(out, "w").write("\n".join(s))
    return out


if __name__ == "__main__":
    # smoke self-test with the measured wait numbers already in hand
    build_bars(
        ["1.0x", "1.5x", "2.0x"],
        {"shared": [14.1, 18.1, 20.1], "independent": [14.8, 19.2, 21.7]},
        "/tmp/bars_test.svg", title="mean wait vs demand", ylabel="mean wait (s)")
    print("wrote /tmp/bars_test.svg")
