"""
plot.py — render training logs as a standalone SVG learning curve.

    python training/plot.py training/runs/*.json -o training/runs/curve.svg

No matplotlib: the whole project draws its own SVG (see signal-decisions.html),
and one hand-built <polyline> per run keeps the output diffable and dependency
-free. Plots rolling episode-return per agent against env steps; higher (less
negative pressure) is better.
"""
from __future__ import annotations

import argparse
import json
import math

W, H = 900, 460
PAD_L, PAD_R, PAD_T, PAD_B = 70, 24, 54, 52
COLORS = ["#E8C068", "#9AAABB", "#7D9A6A", "#C47A5A", "#9A7AB0", "#6B7A8D"]
VOID, BORDER, TMUTED, TBRIGHT = "#111009", "#3D3A30", "#6A6358", "#EDE8DC"


def load(path):
    d = json.load(open(path))
    log = d["log"]
    xs = log["steps"]
    ys = log["ep_ret"]
    pts = [(x, y) for x, y in zip(xs, ys) if y == y]  # drop nan warmup
    return d.get("tag", path), pts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("logs", nargs="+")
    ap.add_argument("-o", "--out", default="training/runs/curve.svg")
    ap.add_argument("--title", default="Signal — shared-policy training")
    args = ap.parse_args()

    series = [load(p) for p in args.logs]
    series = [(t, p) for t, p in series if p]
    allx = [x for _, p in series for x, _ in p]
    ally = [y for _, p in series for _, y in p]
    x0, x1 = 0, max(allx)
    y0, y1 = min(ally), max(ally)
    pad = (y1 - y0) * 0.08 or 1.0
    y0 -= pad; y1 += pad

    def sx(x): return PAD_L + (x - x0) / (x1 - x0 or 1) * (W - PAD_L - PAD_R)
    def sy(y): return H - PAD_B - (y - y0) / (y1 - y0 or 1) * (H - PAD_T - PAD_B)

    s = [f'<svg viewBox="0 0 {W} {H}" xmlns="http://www.w3.org/2000/svg" '
         f'font-family="JetBrains Mono,monospace">',
         f'<rect width="{W}" height="{H}" fill="{VOID}"/>',
         f'<text x="{PAD_L}" y="30" fill="{TBRIGHT}" font-family="Rajdhani,sans-serif" '
         f'font-weight="700" font-size="19" letter-spacing="1">{args.title}</text>']

    # y grid + labels
    for i in range(5):
        gy = y0 + (y1 - y0) * i / 4
        yy = sy(gy)
        s.append(f'<line x1="{PAD_L}" y1="{yy:.1f}" x2="{W-PAD_R}" y2="{yy:.1f}" '
                 f'stroke="{BORDER}" stroke-width="0.6"/>')
        s.append(f'<text x="{PAD_L-8}" y="{yy+4:.1f}" fill="{TMUTED}" font-size="11" '
                 f'text-anchor="end">{gy:.0f}</text>')
    # x labels
    for i in range(5):
        gx = x0 + (x1 - x0) * i / 4
        xx = sx(gx)
        s.append(f'<text x="{xx:.1f}" y="{H-PAD_B+20}" fill="{TMUTED}" font-size="11" '
                 f'text-anchor="middle">{gx/1000:.0f}k</text>')
    s.append(f'<text x="{(W)/2:.0f}" y="{H-8}" fill="{TMUTED}" font-size="12" '
             f'text-anchor="middle">env steps</text>')
    s.append(f'<text x="18" y="{H/2:.0f}" fill="{TMUTED}" font-size="12" '
             f'text-anchor="middle" transform="rotate(-90 18 {H/2:.0f})">'
             f'episode return / agent</text>')

    for i, (tag, pts) in enumerate(series):
        c = COLORS[i % len(COLORS)]
        poly = " ".join(f"{sx(x):.1f},{sy(y):.1f}" for x, y in pts)
        s.append(f'<polyline points="{poly}" fill="none" stroke="{c}" '
                 f'stroke-width="2" opacity="0.95"/>')
        ly = 44 + i * 18
        s.append(f'<line x1="{W-PAD_R-160}" y1="{ly-4}" x2="{W-PAD_R-140}" y2="{ly-4}" '
                 f'stroke="{c}" stroke-width="3"/>')
        s.append(f'<text x="{W-PAD_R-134}" y="{ly}" fill="{TBRIGHT}" font-size="11">'
                 f'{tag}</text>')
    s.append("</svg>")
    open(args.out, "w").write("\n".join(s))
    print(f"wrote {args.out}  ({len(series)} series)")


if __name__ == "__main__":
    main()
