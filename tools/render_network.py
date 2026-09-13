"""
render_network.py — draw a LevelDef's road topology as a legible SVG.

    dotnet Signal.EnvServer/bin/Release/net8.0/Signal.EnvServer.dll --dump sc-couplet > x.json
    python tools/render_network.py x.json -o x.svg --title "..." --subtitle "..."

Reads from the true authored network so the picture is the model, not a mock-up.
Encoded so a scenario is readable at a glance:
  * link thickness/colour  = road hierarchy (thick amber arterial, thin steel side street)
  * arrowhead              = one-way direction (two-way links have none)
  * coral link             = right-turn-only lane (RIRO)
  * node glyph             = control type (circle=signal, square=all-way stop,
                             triangle=yield, small dot=uncontrolled, grey square=boundary)
  * amber glow band        = a heavy origin-destination demand flow
"""
from __future__ import annotations

import argparse
import json

VOID, BORDER, TMUTED, TBRIGHT = "#111009", "#3D3A30", "#6A6358", "#EDE8DC"
AMBER, STEEL, SAGE, CORAL = "#E8C068", "#6B7A8D", "#7D9A6A", "#FF5E3A"
PAD = 76
RIGHT_ONLY = 4          # TurnMask.Right


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("level_json")
    ap.add_argument("-o", "--out", default="network.svg")
    ap.add_argument("--title", default="Signal")
    ap.add_argument("--subtitle", default="")
    args = ap.parse_args()

    d = json.load(open(args.level_json))
    nodes = {n["id"]: n for n in d["network"]["nodes"]}
    links = d["network"]["links"]
    flows = d["demand"]["flows"]

    xs = [n["x"] for n in nodes.values()]; ys = [n["y"] for n in nodes.values()]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    W = 940
    scale = (W - 2 * PAD) / (x1 - x0 or 1)
    H = int((y1 - y0) * scale + 2 * PAD + 20)

    def sx(x): return PAD + (x - x0) * scale
    def sy(y): return H - PAD - (y - y0) * scale     # +y up

    pairset = {(l["from"], l["to"]) for l in links}
    s = [f'<svg viewBox="0 0 {W} {H}" xmlns="http://www.w3.org/2000/svg" '
         f'font-family="JetBrains Mono,monospace">',
         f'<rect width="{W}" height="{H}" fill="{VOID}"/>',
         '<defs>' + "".join(
             f'<marker id="a{cid}" markerWidth="7" markerHeight="7" refX="5" refY="3.5" '
             f'orient="auto"><path d="M0,0 L7,3.5 L0,7 z" fill="{col}"/></marker>'
             for cid, col in [("A", AMBER), ("S", STEEL), ("C", CORAL)]) + '</defs>',
         f'<text x="{PAD}" y="34" fill="{TBRIGHT}" font-family="Rajdhani,sans-serif" '
         f'font-weight="700" font-size="21" letter-spacing="1">{args.title}</text>']
    if args.subtitle:
        s.append(f'<text x="{PAD}" y="54" fill="{TMUTED}" font-size="12">{args.subtitle}</text>')

    # demand bands: only the flows clearly heavier than background, so uniform
    # demand draws nothing and genuine thoroughfares stand out.
    def peak(f): return max(f["rate"]["rates"]) if f["rate"]["rates"] else 0
    peaks = sorted(peak(f) for f in flows)
    maxflow = peaks[-1] if peaks else 1
    median = peaks[len(peaks) // 2] if peaks else 0
    thresh = max(1.4 * median, 0.45 * maxflow)
    for f in sorted(flows, key=peak, reverse=True)[:6]:
        if peak(f) < thresh:
            continue
        a, b = nodes.get(f["origin"]), nodes.get(f["dest"])
        if not a or not b:
            continue
        w = 3 + 16 * peak(f) / maxflow
        s.append(f'<line x1="{sx(a["x"]):.1f}" y1="{sy(a["y"]):.1f}" x2="{sx(b["x"]):.1f}" '
                 f'y2="{sy(b["y"]):.1f}" stroke="{AMBER}" stroke-width="{w:.1f}" '
                 f'opacity="0.10" stroke-linecap="round"/>')

    # links
    for l in links:
        a, b = nodes[l["from"]], nodes[l["to"]]
        fast = l.get("speedLimit", 13.9) > 14.0
        oneway = (l["to"], l["from"]) not in pairset
        boundary = a.get("isBoundary") or b.get("isBoundary")
        right_only = l.get("turns") == RIGHT_ONLY
        if right_only:
            col, mk, wdt = CORAL, "aC", 3.0
        elif boundary:
            col, mk, wdt = BORDER, "aS", 1.4
        elif fast:
            col, mk, wdt = AMBER, "aA", 4.5
        else:
            col, mk, wdt = STEEL, "aS", 2.2
        ax, ay, bx, by = sx(a["x"]), sy(a["y"]), sx(b["x"]), sy(b["y"])
        attrs = f'stroke="{col}" stroke-width="{wdt}" opacity="0.92"'
        if oneway and not boundary:
            dx, dy = bx - ax, by - ay
            L = (dx * dx + dy * dy) ** 0.5 or 1
            bx2, by2 = bx - dx / L * 15, by - dy / L * 15
            s.append(f'<line x1="{ax:.1f}" y1="{ay:.1f}" x2="{bx2:.1f}" y2="{by2:.1f}" '
                     f'{attrs} marker-end="url(#{mk})"/>')
        else:
            s.append(f'<line x1="{ax:.1f}" y1="{ay:.1f}" x2="{bx:.1f}" y2="{by:.1f}" {attrs}/>')

    # nodes by control type
    def glyph(n):
        cx, cy = sx(n["x"]), sy(n["y"])
        ctl = n.get("control", 0)
        if n.get("isBoundary"):
            return f'<rect x="{cx-4:.1f}" y="{cy-4:.1f}" width="8" height="8" fill="{TMUTED}"/>'
        if ctl == 1:   # signal
            return f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="7" fill="{VOID}" stroke="{TBRIGHT}" stroke-width="2"/>'
        if ctl == 2:   # all-way stop
            return f'<rect x="{cx-6:.1f}" y="{cy-6:.1f}" width="12" height="12" fill="{VOID}" stroke="{CORAL}" stroke-width="2"/>'
        if ctl == 4:   # yield
            return (f'<path d="M{cx:.1f},{cy-7:.1f} L{cx+7:.1f},{cy+6:.1f} L{cx-7:.1f},{cy+6:.1f} z" '
                    f'fill="{VOID}" stroke="{SAGE}" stroke-width="2"/>')
        return f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="3" fill="{TMUTED}"/>'
    for n in nodes.values():
        s.append(glyph(n))

    # legend
    lg = [("arterial (fast)", "line", AMBER, 4.5), ("side street", "line", STEEL, 2.2),
          ("right-turn only", "line", CORAL, 3.0), ("→ one-way", "arrow", TBRIGHT, 0),
          ("● signal", "sig", TBRIGHT, 0), ("▪ all-way stop", "stop", CORAL, 0),
          ("▲ yield", "yield", SAGE, 0), ("▪ boundary", "bnd", TMUTED, 0)]
    lx = W - 250
    for i, (lab, kind, col, wd) in enumerate(lg):
        yy = 40 + i * 19
        if kind == "line":
            s.append(f'<line x1="{lx}" y1="{yy}" x2="{lx+22}" y2="{yy}" stroke="{col}" stroke-width="{wd}"/>')
        s.append(f'<text x="{lx+28}" y="{yy+4}" fill="{TBRIGHT}" font-size="11">{lab}</text>')
    s.append("</svg>")
    open(args.out, "w").write("\n".join(s))
    print(f"wrote {args.out}  ({len(nodes)} nodes, {len(links)} links, {len(flows)} flows)")


if __name__ == "__main__":
    main()
