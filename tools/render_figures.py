#!/usr/bin/env python3
"""Render faithful top-down SVGs of the actual simulated intersection from
/tmp/snap.json: real geometry, real queued vehicles, real signal state,
real conflict matrix. Orbitope palette."""
import json
import math

C = dict(void="#111009", surface="#1E1C16", raised="#2C2A22", border="#3D3A30",
         tbright="#EDE8DC", tprimary="#C8C0AE", tsec="#9A9484", tmut="#6A6358",
         amber="#C49A3C", amberb="#E8C068", steel="#6B7A8D", steelb="#9AAABB",
         coral="#FF5E3A", sage="#7D9A6A", terra="#C47A5A", mauve="#9A7AB0")

data = json.load(open("/tmp/snap.json"))
S = 2.5          # px per meter (per-figure override below)
LANE_W = 3.4     # meters
BASE_OFF = 2.4   # meters, right-of-travel separation of directions

def world(x, y, cx, cy):
    return (cx + x * _S[0], cy - y * _S[0])
_S = [2.5]

def link_geom(l, links):
    """Centerline endpoints in world meters with direction/lane fan offsets
    (mirrors godot NetworkView exactly)."""
    ax, ay, bx, by = l["fx"], l["fy"], l["tx"], l["ty"]
    dx, dy = bx - ax, by - ay
    n = math.hypot(dx, dy)
    dx, dy = dx / n, dy / n
    # right of travel in world coords (y-up): (dy, -dx)
    rx, ry = dy, -dx
    bundle = sorted(o["id"] for o in links if o["from"] == l["from"] and o["to"] == l["to"])
    li = bundle.index(l["id"])
    lane = (li - (len(bundle) - 1) / 2) if len(bundle) > 1 else 0.0
    off = BASE_OFF + lane * (LANE_W * 1.02)
    ox, oy = rx * off, ry * off
    return (ax + ox, ay + oy, bx + ox, by + oy, dx, dy, rx, ry)

def slab(p, w):
    ax, ay, bx, by, dx, dy, rx, ry = p
    hw = w / 2
    pts = [(ax + rx * hw, ay + ry * hw), (bx + rx * hw, by + ry * hw),
           (bx - rx * hw, by - ry * hw), (ax - rx * hw, ay - ry * hw)]
    return pts

def poly(pts, cx, cy, fill, extra=""):
    s = " ".join(f"{world(x, y, cx, cy)[0]:.1f},{world(x, y, cx, cy)[1]:.1f}" for x, y in pts)
    return f'<polygon points="{s}" fill="{fill}" {extra}/>'

def line(x1, y1, x2, y2, cx, cy, stroke, w, extra=""):
    a = world(x1, y1, cx, cy); b = world(x2, y2, cx, cy)
    return (f'<line x1="{a[0]:.1f}" y1="{a[1]:.1f}" x2="{b[0]:.1f}" y2="{b[1]:.1f}" '
            f'stroke="{stroke}" stroke-width="{w}" {extra}/>')

def lerp_col(c1, c2, t):
    c1 = [int(c1[i:i+2], 16) for i in (1, 3, 5)]
    c2 = [int(c2[i:i+2], 16) for i in (1, 3, 5)]
    return "#" + "".join(f"{round(a + (b - a) * t):02x}" for a, b in zip(c1, c2))

_clip_n = [0]
def render_snapshot(snap, cx, cy, w, h, title, show_gates=True, show_bars=True):
    links = snap["links"]
    allowed = set(snap["allowedInLinks"])
    _clip_n[0] += 1
    cid = f"clip{_clip_n[0]}"
    out = [f'<clipPath id="{cid}"><rect x="{cx-w/2}" y="{cy-h/2}" width="{w}" height="{h}"/></clipPath>']
    out.append(f'<g font-family="JetBrains Mono,monospace" clip-path="url(#{cid})">')
    out.append(f'<rect x="{cx-w/2}" y="{cy-h/2}" width="{w}" height="{h}" fill="{C["void"]}"/>')

    geoms = {l["id"]: link_geom(l, links) for l in links}

    # --- road slabs ---
    for l in links:
        out.append(poly(slab(geoms[l["id"]], LANE_W), cx, cy, C["raised"]))
    # fork taper polygons: connect arm slab end to bundle outer edges
    for l in links:
        if l["id"] > 4:      # arms are 1..4
            continue
        bundle = [o for o in links if o["from"] == l["to"] and o["to"] == 0]
        if len(bundle) < 2:
            continue
        ap = geoms[l["id"]]
        edges = []
        for o in bundle:
            g = geoms[o["id"]]
            edges += [(g[0] + g[6] * LANE_W/2, g[1] + g[7] * LANE_W/2),
                      (g[0] - g[6] * LANE_W/2, g[1] - g[7] * LANE_W/2)]
        armend = [(ap[2] + ap[6] * LANE_W/2, ap[3] + ap[7] * LANE_W/2),
                  (ap[2] - ap[6] * LANE_W/2, ap[3] - ap[7] * LANE_W/2)]
        # convex-ish hull by angle around midpoint
        pts = edges + armend
        mx = sum(p[0] for p in pts) / len(pts); my = sum(p[1] for p in pts) / len(pts)
        pts.sort(key=lambda p: math.atan2(p[1] - my, p[0] - mx))
        out.append(poly(pts, cx, cy, C["raised"]))
    # intersection box
    box = 11.5
    out.append(poly([(-box, -box), (box, -box), (box, box), (-box, box)], cx, cy, C["raised"]))

    # --- lane separators (dashed) between bundle lanes ---
    for l in links:
        bundle = sorted(o["id"] for o in links if o["from"] == l["from"] and o["to"] == l["to"])
        if len(bundle) > 1 and l["id"] != bundle[-1]:
            g = geoms[l["id"]]
            sx, sy = g[0] - g[6] * LANE_W * 0.51, g[1] - g[7] * LANE_W * 0.51
            ex, ey = g[2] - g[6] * LANE_W * 0.51, g[3] - g[7] * LANE_W * 0.51
            out.append(line(sx, sy, ex, ey, cx, cy, C["border"], 1.1,
                            'stroke-dasharray="5,5"'))

    # --- vehicles (real positions), steel->amber by wait like the game ---
    for l in links:
        g = geoms[l["id"]]
        for v in l["vehicles"]:
            t = min(v["wait"] / 60.0, 1.0)
            col = lerp_col(C["steel"], C["amberb"], t)
            fx = g[0] + g[4] * (v["pos"] - v["len"] / 2)
            fy = g[1] + g[5] * (v["pos"] - v["len"] / 2)
            px, py = world(fx, fy, cx, cy)
            ang = -math.degrees(math.atan2(g[5], g[4]))
            out.append(f'<rect x="{px - v["len"]*S/2:.1f}" y="{py - 1.0*S:.1f}" '
                       f'width="{v["len"]*S:.1f}" height="{2.0*S:.1f}" rx="1.5" fill="{col}" '
                       f'transform="rotate({ang:.1f} {px:.1f} {py:.1f})"/>')

    # --- stop bars on center in-links, colored by real phase state ---
    for l in links:
        if not show_bars or l["to"] != 0:
            continue
        g = geoms[l["id"]]
        bx_, by_ = g[2] - g[4] * 1.5, g[3] - g[5] * 1.5
        col = C["sage"] if (l["id"] in allowed and snap["state"] == "Green") else \
              (C["amber"] if snap["state"] == "Yellow" else C["terra"])
        out.append(line(bx_ - g[6] * LANE_W/2, by_ - g[7] * LANE_W/2,
                        bx_ + g[6] * LANE_W/2, by_ + g[7] * LANE_W/2, cx, cy, col, 4.5))

    # --- gate counts, pinned to the panel edge along each arm ---
    if show_gates:
        for nid, cnt in snap["gates"].items():
            if cnt == 0:
                continue
            bl = next(l for l in links if str(l["from"]) == str(nid))
            px, py = world(bl["fx"], bl["fy"], cx, cy)
            px = min(max(px, cx - w/2 + 22), cx + w/2 - 22)
            py = min(max(py, cy - h/2 + 60), cy + h/2 - 22)
            out.append(f'<circle cx="{px:.0f}" cy="{py:.0f}" r="13" fill="{C["surface"]}" stroke="{C["amber"]}"/>')
            out.append(f'<text x="{px:.0f}" y="{py+4:.0f}" text-anchor="middle" '
                       f'fill="{C["amberb"]}" font-size="11">+{cnt}</text>')

    out.append(f'<text x="{cx}" y="{cy - h/2 + 20}" text-anchor="middle" fill="{C["tbright"]}" '
               f'font-family="Rajdhani,sans-serif" font-weight="700" font-size="15" '
               f'letter-spacing="1">{title}</text>')
    if title:
        out.append(f'<text x="{cx}" y="{cy - h/2 + 38}" text-anchor="middle" fill="{C["tsec"]}" '
                   f'font-size="11">t={snap["t"]:.0f}s · phase {snap["phase"]} {snap["state"]}</text>')
    out.append('</g>')
    return "\n".join(out)

# ============================================================
# FIG 1: two real snapshots side by side
# ============================================================
snapA, snapB = data["snaps"][0], data["snaps"][1]
_S[0] = 2.5
fig1 = ['<svg viewBox="0 0 1000 560" xmlns="http://www.w3.org/2000/svg">',
        f'<rect width="1000" height="560" fill="{C["void"]}"/>',
        render_snapshot(snapA, 255, 285, 490, 545, "NS THROUGH GREEN"),
        render_snapshot(snapB, 755, 285, 490, 545, "NS PROTECTED LEFTS GREEN"),
        f'<line x1="500" y1="15" x2="500" y2="545" stroke="{C["border"]}"/>']
# annotations on left panel: bay/through/fork/arm callouts (world->screen of panel A)
def annA(x, y, tx, ty, label, col=C["tsec"]):
    px, py = world(x, y, 255, 285); qx, qy = world(tx, ty, 255, 285)
    return (f'<line x1="{px:.0f}" y1="{py:.0f}" x2="{qx:.0f}" y2="{qy:.0f}" stroke="{C["border"]}" stroke-width="1"/>'
            f'<text x="{qx:.0f}" y="{qy - 4:.0f}" text-anchor="middle" fill="{col}" '
            f'font-family="JetBrains Mono,monospace" font-size="11">{label}</text>')
fig1 += [annA(-7.2, 30, -48, 38, "left bay", C["amberb"]),
         annA(2.2, 30, 40, 44, "through x2", C["steelb"]),
         annA(0, 61, 44, 68, "fork", C["tprimary"]),
         annA(0, 100, -48, 76, "arm", C["tprimary"])]
fig1.append('</svg>')

# ============================================================
# FIG 2: three stacked panels, big legible movement arrows
# ============================================================
mv = data["movements"]
mvd = {m["idx"]: m for m in mv}
snapB_links = data["snaps"][1]["links"]
geoms0 = {l["id"]: link_geom(l, snapB_links) for l in snapB_links}
turncol = dict(Left=C["amberb"], Through=C["steelb"], Right=C["sage"])
BOXE = 12.0

def entry_pt(link_id):
    g = geoms0[link_id]
    return (g[2] - g[4] * BOXE, g[3] - g[5] * BOXE, g[4], g[5])

def exit_pt(link_id):
    g = geoms0[link_id]
    return (g[0] + g[4] * BOXE, g[1] + g[5] * BOXE, g[4], g[5])

def mv_ctrl(m):
    ax, ay, adx, ady = entry_pt(m["inLink"])
    bx, by, bdx, bdy = exit_pt(m["outLink"])
    k = 11.0
    return (ax, ay), (ax + adx * k, ay + ady * k), (bx - bdx * k, by - bdy * k), (bx, by)

def bez(t, a, c1, c2, b):
    return ((1-t)**3*a[0] + 3*(1-t)**2*t*c1[0] + 3*(1-t)*t*t*c2[0] + t**3*b[0],
            (1-t)**3*a[1] + 3*(1-t)**2*t*c1[1] + 3*(1-t)*t*t*c2[1] + t**3*b[1])

def mv_path_d(m, cx, cy):
    a, c1, c2, b = mv_ctrl(m)
    p = [world(*q, cx, cy) for q in (a, c1, c2, b)]
    return ("M %.1f %.1f C %.1f %.1f %.1f %.1f %.1f %.1f" %
            (p[0][0], p[0][1], p[1][0], p[1][1], p[2][0], p[2][1], p[3][0], p[3][1]))

def seg_hit(c1, c2):
    p1 = [bez(t/30.0, *c1) for t in range(31)]
    p2 = [bez(t/30.0, *c2) for t in range(31)]
    def cr(o, p, q): return (p[0]-o[0])*(q[1]-o[1])-(p[1]-o[1])*(q[0]-o[0])
    for i in range(30):
        for j in range(30):
            a, b = p1[i], p1[i+1]; c, d = p2[j], p2[j+1]
            if ((cr(c,d,a) > 0) != (cr(c,d,b) > 0)) and ((cr(a,b,c) > 0) != (cr(a,b,d) > 0)):
                return ((a[0]+b[0])/2, (a[1]+b[1])/2)
    return None

phase_thru = list(data["phases"][0])
phase_left = list(data["phases"][1])

def big_panel(cy, idxs, title, subtitle, show_conflicts=False, dim=None):
    cx, w, h = 500, 990, 360
    o = []
    base = json.loads(json.dumps(data["snaps"][1]))
    for l in base["links"]:
        l["vehicles"] = []
    base["gates"] = {}
    o.append('<g opacity="0.5">' + render_snapshot(base, cx, cy, w, h, "", show_gates=False, show_bars=False) + '</g>')
    cl = "pclip%d" % cy
    o.append('<clipPath id="%s"><rect x="%d" y="%d" width="%d" height="%d"/></clipPath>' % (cl, cx-w//2, cy-h//2, w, h))
    o.append('<g clip-path="url(#%s)">' % cl)
    def draw(i, wid, op, arrow):
        m = mvd[i]
        d = mv_path_d(m, cx, cy)
        if op > 0.5:
            o.append('<path d="%s" fill="none" stroke="%s" stroke-width="%.1f" opacity="0.9"/>' % (d, C["void"], wid + 4))
        o.append('<path d="%s" fill="none" stroke="%s" stroke-width="%.1f" opacity="%.2f"%s/>' %
                 (d, turncol[m["turn"]], wid, op, (' marker-end="url(#A%s)"' % m["turn"]) if arrow else ''))
    if dim:
        for i in dim:
            draw(i, 3.0, 0.28, False)
    for i in idxs:
        draw(i, 5.0, 0.95, True)
    ncross = 0
    if show_conflicts:
        seen = []
        for a, b in data["conflicts"]:
            if not ((a in phase_left and b in phase_thru) or (a in phase_thru and b in phase_left)):
                continue
            hit = seg_hit(mv_ctrl(mvd[a]), mv_ctrl(mvd[b]))
            if not hit:
                continue
            px, py = world(hit[0], hit[1], cx, cy)
            if any(abs(px-q[0]) < 12 and abs(py-q[1]) < 12 for q in seen):
                continue
            seen.append((px, py))
            o.append('<circle cx="%.0f" cy="%.0f" r="11" fill="%s" fill-opacity="0.85" stroke="%s" stroke-width="3"/>' % (px, py, C["void"], C["coral"]))
            o.append('<line x1="%.0f" y1="%.0f" x2="%.0f" y2="%.0f" stroke="%s" stroke-width="2.8"/>' % (px-5.5, py-5.5, px+5.5, py+5.5, C["coral"]))
            o.append('<line x1="%.0f" y1="%.0f" x2="%.0f" y2="%.0f" stroke="%s" stroke-width="2.8"/>' % (px-5.5, py+5.5, px+5.5, py-5.5, C["coral"]))
        ncross = len(seen)
    o.append('</g>')
    o.append('<text x="24" y="%d" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="17" letter-spacing="1">%s</text>' % (cy-h//2+26, C["tbright"], title))
    o.append('<text x="24" y="%d" fill="%s" font-family="JetBrains Mono,monospace" font-size="11.5">%s</text>' % (cy-h//2+46, C["tsec"], subtitle))
    if show_conflicts:
        o.append('<text x="976" y="%d" text-anchor="end" fill="%s" font-family="JetBrains Mono,monospace" font-size="13">%d crossings — cannot share a phase</text>' % (cy-h//2+30, C["coral"], ncross))
    return "\n".join(o)

_S[0] = 10.5
DEFS2 = ('<defs>'
        '<marker id="ALeft" markerWidth="9" markerHeight="9" refX="6" refY="4.5" orient="auto"><path d="M0,0 L9,4.5 L0,9 z" fill="%s"/></marker>'
        '<marker id="AThrough" markerWidth="9" markerHeight="9" refX="6" refY="4.5" orient="auto"><path d="M0,0 L9,4.5 L0,9 z" fill="%s"/></marker>'
        '<marker id="ARight" markerWidth="9" markerHeight="9" refX="6" refY="4.5" orient="auto"><path d="M0,0 L9,4.5 L0,9 z" fill="%s"/></marker>'
        '</defs>') % (C["amberb"], C["steelb"], C["sage"])

fig2 = ['<svg viewBox="0 0 1000 1180" xmlns="http://www.w3.org/2000/svg">',
        '<rect width="1000" height="1180" fill="%s"/>' % C["void"], DEFS2,
        '<text x="500" y="30" text-anchor="middle" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="18" letter-spacing="1.5">WHY PROTECTED LEFTS NEED THEIR OWN PHASE</text>' % C["tbright"],
        big_panel(240, phase_thru, "PHASE 0 — NS THROUGH + RIGHTS", "both directions · two through lanes each, rights peel to their exits"),
        big_panel(620, phase_left, "PHASE 1 — NS PROTECTED LEFTS", "bay lefts only · each bay curve exits across the opposing roadway"),
        big_panel(1000, phase_left, "IF RUN TOGETHER", "phase 1 (bright) over phase 0 (dimmed) — coral = crossings from the actual conflict matrix", True, dim=phase_thru),
        '<text x="500" y="1170" text-anchor="middle" fill="%s" font-family="JetBrains Mono,monospace" font-size="11">a bay-left never crosses its OWN throughs — shared origin, diverging paths — so those may share a phase; that is chord geometry, not a special case</text>' % C["tmut"],
        '</svg>']

# ============================================================
# FIG 3: exit side — lane drop today vs naive weave vs aligned (road-slab geometry)
# ============================================================
def mini_road(x0, y0, lanes_in, lanes_out, paths, title, note, notecol):
    """A small road-slab rendering: lanes_in slabs entering a box, lanes_out leaving,
    paths = list of (inIdx,outIdx,color)."""
    o = [f'<g font-family="JetBrains Mono,monospace">']
    LW = 13; GAP = 1.5; BL = 96; BOX = 46
    def lane_y(i, n): return y0 + (i - (n-1)/2) * (LW + GAP)
    for i in range(lanes_in):
        y = lane_y(i, lanes_in)
        o.append(f'<rect x="{x0}" y="{y-LW/2}" width="{BL}" height="{LW}" fill="{C["raised"]}"/>')
    for i in range(lanes_out):
        y = lane_y(i, lanes_out)
        o.append(f'<rect x="{x0+BL+BOX}" y="{y-LW/2}" width="{BL}" height="{LW}" fill="{C["raised"]}"/>')
    o.append(f'<rect x="{x0+BL}" y="{y0-(max(lanes_in,lanes_out))*(LW+GAP)/2-2}" width="{BOX}" '
             f'height="{(max(lanes_in,lanes_out))*(LW+GAP)+4}" fill="{C["raised"]}"/>')
    for i, j, col in paths:
        y1 = lane_y(i, lanes_in); y2 = lane_y(j, lanes_out)
        o.append(f'<path d="M {x0+BL-6} {y1} C {x0+BL+BOX*0.45} {y1} {x0+BL+BOX*0.55} {y2} '
                 f'{x0+BL+BOX+8} {y2}" fill="none" stroke="{col}" stroke-width="2.6"/>')
    # weave crossings
    for a in range(len(paths)):
        for b in range(a+1, len(paths)):
            i1, j1, _ = paths[a]; i2, j2, _ = paths[b]
            if (i1 - i2) * (j1 - j2) < 0:
                mx = x0 + BL + BOX/2; my = (lane_y(i1,lanes_in)+lane_y(j1,lanes_out)+lane_y(i2,lanes_in)+lane_y(j2,lanes_out))/4
                o.append(f'<circle cx="{mx:.0f}" cy="{my:.0f}" r="6" fill="none" stroke="{C["coral"]}" stroke-width="2.2"/>')
    o.append(f'<text x="{x0+(2*BL+BOX)/2}" y="{y0-52}" text-anchor="middle" fill="{C["tbright"]}" '
             f'font-family="Rajdhani,sans-serif" font-weight="700" font-size="13" letter-spacing="1">{title}</text>')
    o.append(f'<text x="{x0+(2*BL+BOX)/2}" y="{y0+56}" text-anchor="middle" fill="{notecol}" font-size="11">{note}</text>')
    o.append('</g>')
    return "\n".join(o)

_S[0] = 2.5
fig3 = [f'<svg viewBox="0 0 1000 200" xmlns="http://www.w3.org/2000/svg">',
        f'<rect width="1000" height="200" fill="{C["void"]}"/>',
        mini_road(30, 105, 2, 1, [(0, 0, C["steelb"]), (1, 0, C["steelb"])],
                  "TODAY: 2 → 1 LANE DROP",
                  "zipper merge — honest, fine per-intersection", C["tsec"]),
        mini_road(370, 105, 2, 2, [(0, 1, C["coral"]), (1, 0, C["coral"])],
                  "NAIVE 2 → 2: IN-BOX WEAVE",
                  "invisible to the conflict matrix", C["coral"]),
        mini_road(710, 105, 2, 2, [(0, 0, C["sage"]), (1, 1, C["sage"])],
                  "M5 FIX: laneIndex i → i",
                  "width changes: fork/merge outside the box", C["sage"]),
        '</svg>']

# ============================================================
# FIG 4: what the agent sees — real obs values on the real scene
# ============================================================
snapA2 = data["snaps"][0]
obs = snapA2["obs"]
import base64 as _b64
mask = list(_b64.b64decode(snapA2["mask"])) if isinstance(snapA2["mask"], str) else snapA2["mask"]
_S[0] = 3.4

fig4 = ['<svg viewBox="0 0 1000 640" xmlns="http://www.w3.org/2000/svg">',
        '<rect width="1000" height="640" fill="%s"/>' % C["void"],
        '<text x="500" y="28" text-anchor="middle" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="18" letter-spacing="1.5">WHAT THE AGENT SEES — REAL VALUES, THIS EXACT MOMENT</text>' % C["tbright"]]

CX4, CY4, W4, H4 = 300, 345, 560, 520
fig4.append(render_snapshot(json.loads(json.dumps(snapA2)), CX4, CY4, W4, H4,
                            "", show_gates=True))
fig4.append('<text x="20" y="630" fill="%s" font-family="JetBrains Mono,monospace" font-size="11">t=%.0fs · phase %d green %.1fs · this tick reward %.3f (pressure, arm+gate folded)</text>'
            % (C["tsec"], snapA2["t"], snapA2["phase"], snapA2["timeInPhase"], snapA2["tickReward"]))

# approach value cards pinned N/E/S/W around the scene (slot order N,E,S,W)
slots = [("N", 118, 58, 0), ("E", 400, 462, 6), ("S", 118, 508, 12), ("W", 16, 258, 18)]
for name, x, y, o in slots:
    bq, bw, tq, tw, hb, vd = obs[o:o+6]
    card_w, card_h = 168, 92
    fig4.append('<rect x="%d" y="%d" width="%d" height="%d" rx="5" fill="%s" fill-opacity="0.95" stroke="%s"/>' % (x, y, card_w, card_h, C["surface"], C["border"]))
    fig4.append('<text x="%d" y="%d" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="13">%s APPROACH  obs[%d..%d]</text>' % (x+10, y+18, C["tbright"], name, o, o+5))
    rows = [("bayQueue", bq, C["amberb"]), ("bayHeadWait", bw, C["amberb"]),
            ("thrQueue", tq, C["steelb"]), ("thrHeadWait", tw, C["steelb"])]
    for i, (label, val, col) in enumerate(rows):
        yy = y + 33 + i * 15
        fig4.append('<text x="%d" y="%d" fill="%s" font-family="JetBrains Mono,monospace" font-size="10.5">%s</text>' % (x+10, yy, C["tsec"], label))
        bw_px = 56.0
        fig4.append('<rect x="%d" y="%d" width="%.1f" height="7" fill="%s" opacity="0.28"/>' % (x+96, yy-7, bw_px, col))
        fig4.append('<rect x="%d" y="%d" width="%.1f" height="7" fill="%s"/>' % (x+96, yy-7, bw_px*max(val,0), col))
        fig4.append('<text x="%d" y="%d" fill="%s" font-family="JetBrains Mono,monospace" font-size="10.5" text-anchor="end">%.2f</text>' % (x+card_w-6, yy, C["tprimary"], val))

# right column: phase, time, mask, neighbors, gate note
RX = 700
fig4.append('<text x="%d" y="90" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="14" letter-spacing="1">PHASE ONE-HOT  obs[24..31]</text>' % (RX, C["tbright"]))
for p in range(8):
    on = obs[24+p] > 0.5
    xx = RX + p*32
    fig4.append('<circle cx="%d" cy="112" r="9" fill="%s" stroke="%s"/>' % (xx+9, C["amber"] if on else C["raised"], C["border"]))
    fig4.append('<text x="%d" y="140" text-anchor="middle" fill="%s" font-family="JetBrains Mono,monospace" font-size="10">%d</text>' % (xx+9, C["tmut"], p))
fig4.append('<text x="%d" y="175" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="14" letter-spacing="1">TIME IN PHASE  obs[32]</text>' % (RX, C["tbright"]))
fig4.append('<rect x="%d" y="185" width="200" height="9" fill="%s"/>' % (RX, C["raised"]))
fig4.append('<rect x="%d" y="185" width="%.1f" height="9" fill="%s"/>' % (RX, 200*min(obs[32]/2.0,1.0), C["amber"]))
fig4.append('<text x="980" y="212" text-anchor="end" fill="%s" font-family="JetBrains Mono,monospace" font-size="11">%.2f x minGreen (%.1f / 5.0s)</text>' % (C["tprimary"], obs[32], obs[32]*5.0))

fig4.append('<text x="%d" y="235" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="14" letter-spacing="1">ACTION MASK  Discrete(8)</text>' % (RX, C["tbright"]))
for p in range(8):
    legal = mask[p] == 1
    xx = RX + p*34
    col = C["sage"] if legal else C["raised"]
    fig4.append('<rect x="%d" y="246" width="27" height="22" rx="3" fill="%s" stroke="%s"/>' % (xx, col, C["border"]))
    fig4.append('<text x="%d" y="261" text-anchor="middle" fill="%s" font-family="JetBrains Mono,monospace" font-size="11" font-weight="bold">%d</text>' % (xx+13, C["void"] if legal else C["tmut"], p))
legal_list = [p for p in range(8) if mask[p] == 1]
cap = ("only phase %d legal - min-green not yet served (%.1f of %.0fs)"
       % (legal_list[0], obs[32]*5.0, 5.0)) if len(legal_list) == 1 else       ("legal: %s - min-green served; 4-7 do not exist here" % ",".join(map(str, legal_list)))
fig4.append('<text x="%d" y="288" fill="%s" font-family="JetBrains Mono,monospace" font-size="10.5">%s</text>' % (RX, C["tsec"], cap))

fig4.append('<text x="%d" y="330" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="14" letter-spacing="1">NEIGHBORS  obs[33..104]</text>' % (RX, C["tbright"]))
nb_any = any(obs[33+s*18+17] > 0.5 for s in range(4))
fig4.append('<rect x="%d" y="340" width="270" height="34" rx="4" fill="%s" stroke="%s"/>' % (RX, C["raised"], C["border"]))
fig4.append('<text x="%d" y="361" fill="%s" font-family="JetBrains Mono,monospace" font-size="10.5">%s</text>'
            % (RX+10, C["tsec"], "all zeros - single four-way, no upstream nodes" if not nb_any else "populated"))

fig4.append('<text x="%d" y="415" fill="%s" font-family="Rajdhani,sans-serif" font-weight="700" font-size="14" letter-spacing="1">READ THE SCENE BACK</text>' % (RX, C["tbright"]))
for i, t in enumerate([
    "thrQueue folds the arm + gate badges in:",
    "queued demand the stop line cannot see.",
    "bay rows: lefts held while throughs run.",
    "v1 collapsed each approach to ONE number;",
    "these cards are the fix."]):
    fig4.append('<text x="%d" y="%d" fill="%s" font-family="JetBrains Mono,monospace" font-size="11">%s</text>' % (RX, 436+i*17, C["tprimary"] if i<3 else C["tsec"], t))
fig4.append('</svg>')

svgs = {"FIG1": "\n".join(fig1), "FIG2": "\n".join(fig2), "FIG3": "\n".join(fig3), "FIG4": "\n".join(fig4)}
for k, v in svgs.items():
    open(f"/tmp/{k}.svg", "w").write(v)
print("rendered:", {k: len(v) for k, v in svgs.items()})
