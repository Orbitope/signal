"""
build_report.py — assemble the scenario report (self-contained HTML) from the
rendered maps + prose. Reads training/runs/scenarios/*.svg inline so the page is
portable. Run after render_network.py has produced the maps.
"""
import os

RUNS = "training/runs/scenarios"
OUT = "training/runs/scenario-report.html"


def svg(name):
    with open(os.path.join(RUNS, name)) as f:
        return f.read()


# ---- the seven live scenarios (map + prose) -------------------------------
LIVE = [
    ("sc-couplet.svg", "1 · One-way couplet grid", "▲ coord  ·  🟢 built  ·  25 signals",
     "Every street is one-way and parallel streets run opposite directions — a "
     "Manhattan-style couplet on both axes. Left turns are banned everywhere "
     "(through + right only), the real rule that keeps one-ways flowing.",
     "The whole payoff is <b>green-wave progression</b>: phase <i>offsets</i> "
     "between adjacent signals so a platoon rides a wall of greens. That is the "
     "canonical thing a neighbour-aware shared policy can learn and a blind "
     "per-intersection policy structurally cannot. The cleanest coordination test."),

    ("sc-onewaypair.svg", "2 · One-way pair + thoroughfares", "▲ coord  ·  🟢 built  ·  20 signals",
     "Two adjacent one-ways running opposite directions (the amber-glow couplet, "
     "rows 2–3) crossed by two-way <b>thoroughfares</b> (the vertical arterials). "
     "No left turns off the arterials.",
     "The signals must run two coupled rhythms at once — progress the couplet "
     "east/west <i>and</i> platoon-serve the heavy thoroughfare crossings. "
     "Independent agents can't phase-lock the couplet against the arterial surges. "
     "This is your exact ask and the best single headline scenario."),

    ("sc-riro.svg", "3 · Access-managed arterial (RIRO)", "● coord  ·  🟢 built  ·  18 signals",
     "A fast two-way arterial (amber) where every side street is "
     "<b>right-in / right-out only</b> — the coral links. A car on a side street "
     "can only turn right onto the arterial; it cannot cross or turn left.",
     "The turn ban <b>reshapes routing</b>, not just phasing: the turn-aware router "
     "sends blocked trips right and around the block automatically, concentrating "
     "load onto specific right-turn pockets the signals must learn to serve. A clean "
     "demo that banning turns changes where the traffic goes."),

    ("sc-diverge.svg", "5 · Diverge / converge couplet", "● coord  ·  🟢 built  ·  20 signals",
     "A two-way arterial <b>spine</b> (centre column) flanked by a one-way pair — "
     "northbound on one side, southbound on the other — so traffic splits around "
     "the block and rejoins. Lefts banned on the one-ways.",
     "Merge / diverge metering: the join signals must not starve or overfill either "
     "leg. Tests coordination across an <i>asymmetric</i> topology rather than a "
     "regular grid — the split/rejoin points are where a learned policy earns its keep."),

    ("sc-tidal.svg", "6 · Tidal one-way pair", "● coord  ·  🟢 built  ·  20 signals",
     "The same couplet as #2, but demand is <b>time-varying</b>: the eastbound leg "
     "peaks early and drains, the westbound leg does the opposite — a rush-hour tide "
     "reversing the dominant direction across the episode.",
     "Non-stationary <i>and</i> directional. The policy must shift green split toward "
     "whichever way is surging and shift it back later — something a fixed-time plan "
     "cannot track. Extends the earlier corridor-rush result with real structure."),

    ("sc-platoon.svg", "7 · Platoon surge", "● coord  ·  🟢 built  ·  25 signals",
     "A one-way grid fed by a <b>ramp</b> boundary (lower-left) that releases sharp "
     "platoons — short high-rate pulses, not smooth Poisson arrivals (the amber band "
     "traces the platoon's path across the grid).",
     "Bursty, correlated arrivals. A signal must flush a platoon then recover, and the "
     "downstream signals must <b>catch the platoon</b> — progression again, but triggered "
     "by demand rather than baked into offsets. Tests robustness to non-Poisson load."),

    ("sc-mixed.svg", "8 · Heterogeneous control", "▲ coord  ·  🟢 built  ·  16 signals",
     "A signalized grid with an arterial, but several minor crossings are not signals: "
     "<b>all-way stops</b> (coral squares) and a <b>yield</b> (green triangle). Only the "
     "signals are learning agents; the stops/yields run their own fixed rules.",
     "The observation already carries each neighbour's <b>control type</b> — this is the "
     "one scenario that exercises it. The shared policy can learn to lean on a "
     "stop-controlled neighbour differently than a signalized one; an independent agent "
     "is blind to that. Coordination <i>across</i> control types."),

    ("sc-diagonal.svg", "10 · Diagonal arterial  (now live — schema v3)",
     "▲ coord  ·  🟢 built  ·  24 signals",
     "A one-way <b>diagonal</b> thoroughfare cutting NE across the grid, so where it "
     "passes through a junction it adds a fifth (SW) leg — a 5-leg intersection with an "
     "acute-angle crossing. Only right turns are allowed off the diagonal. Building this "
     "forced the observation schema to grow from <b>4 to 8 octant approach slots</b> "
     "(v3, obs 105→233) and the diagonal nodes to a per-approach 5-phase plan; ordinary "
     "4-leg nodes are unchanged.",
     "The diagonal is a tightly-coupled arterial chain (like the corridor), so the green "
     "wave should pay — but its 5-leg nodes are exactly where a 1-hop MLP view is weakest "
     "and where a coupling-aware <b>attention/GNN</b> policy earns its place. The scenario "
     "the whole ladder was pointing at."),
]

# ---- three that need schema / phase work (schematics) ---------------------
def schem_superstreet():
    return f'''<svg viewBox="0 0 900 260" xmlns="http://www.w3.org/2000/svg" font-family="JetBrains Mono,monospace">
<rect width="900" height="260" fill="#111009"/>
<text x="20" y="28" fill="#EDE8DC" font-family="Rajdhani,sans-serif" font-weight="700" font-size="17">4 · Superstreet / RCUT  (schematic — needs hand-authored phases)</text>
<line x1="40" y1="150" x2="860" y2="150" stroke="#E8C068" stroke-width="6"/>
<text x="60" y="142" fill="#6A6358" font-size="10">arterial (through both ways)</text>
{"".join(f'<line x1="{x}" y1="60" x2="{x}" y2="138" stroke="#6B7A8D" stroke-width="2.2"/><line x1="{x}" y1="162" x2="{x}" y2="240" stroke="#6B7A8D" stroke-width="2.2"/>' for x in (250,450,650))}
<!-- cross traffic can't go through: right onto arterial, U-turn downstream, back -->
{"".join(f'<path d="M{x},138 q18,4 30,-6" fill="none" stroke="#FF5E3A" stroke-width="2.5" marker-end="url(#s)"/>' for x in (250,450,650))}
<path d="M760,150 a26,26 0 1 1 -8,-24" fill="none" stroke="#FF5E3A" stroke-width="2.5"/>
<circle cx="250" cy="150" r="6" fill="#111009" stroke="#EDE8DC" stroke-width="2"/>
<circle cx="450" cy="150" r="6" fill="#111009" stroke="#EDE8DC" stroke-width="2"/>
<circle cx="650" cy="150" r="6" fill="#111009" stroke="#EDE8DC" stroke-width="2"/>
<circle cx="770" cy="150" r="6" fill="#111009" stroke="#7D9A6A" stroke-width="2"/>
<defs><marker id="s" markerWidth="7" markerHeight="7" refX="5" refY="3.5" orient="auto"><path d="M0,0 L7,3.5 L0,7 z" fill="#FF5E3A"/></marker></defs>
<text x="700" y="120" fill="#6A6358" font-size="10">median U-turn</text>
</svg>'''


def schem_stagger():
    return '''<svg viewBox="0 0 900 240" xmlns="http://www.w3.org/2000/svg" font-family="JetBrains Mono,monospace">
<rect width="900" height="240" fill="#111009"/>
<text x="20" y="28" fill="#EDE8DC" font-family="Rajdhani,sans-serif" font-weight="700" font-size="17">9 · Staggered-T couplet  (schematic — needs offset node geometry)</text>
<defs><marker id="t" markerWidth="7" markerHeight="7" refX="5" refY="3.5" orient="auto"><path d="M0,0 L7,3.5 L0,7 z" fill="#6B7A8D"/></marker></defs>
<line x1="40" y1="110" x2="860" y2="110" stroke="#6B7A8D" stroke-width="3" marker-end="url(#t)"/>
<line x1="860" y1="150" x2="40" y2="150" stroke="#6B7A8D" stroke-width="3" marker-end="url(#t)"/>
<text x="60" y="100" fill="#6A6358" font-size="10">eastbound</text><text x="760" y="172" fill="#6A6358" font-size="10">westbound</text>
<line x1="300" y1="40" x2="300" y2="110" stroke="#6B7A8D" stroke-width="2.2"/>
<line x1="360" y1="150" x2="360" y2="220" stroke="#6B7A8D" stroke-width="2.2"/>
<line x1="600" y1="40" x2="600" y2="110" stroke="#6B7A8D" stroke-width="2.2"/>
<line x1="660" y1="150" x2="660" y2="220" stroke="#6B7A8D" stroke-width="2.2"/>
<circle cx="300" cy="110" r="6" fill="#111009" stroke="#EDE8DC" stroke-width="2"/>
<circle cx="360" cy="150" r="6" fill="#111009" stroke="#EDE8DC" stroke-width="2"/>
<circle cx="600" cy="110" r="6" fill="#111009" stroke="#EDE8DC" stroke-width="2"/>
<circle cx="660" cy="150" r="6" fill="#111009" stroke="#EDE8DC" stroke-width="2"/>
<text x="330" y="200" fill="#6A6358" font-size="10">cross streets meet the couplet OFFSET — a through trip jogs left-then-right</text>
</svg>'''


def schem_diagonal():
    grid = "".join(
        f'<line x1="{120+c*120}" y1="60" x2="{120+c*120}" y2="200" stroke="#3D3A30" stroke-width="1.4"/>'
        for c in range(6)) + "".join(
        f'<line x1="120" y1="{60+r*70}" x2="720" y2="{60+r*70}" stroke="#3D3A30" stroke-width="1.4"/>'
        for r in range(3))
    circles = "".join(
        f'<circle cx="{120+c*120}" cy="{60+r*70}" r="5" fill="#111009" stroke="#EDE8DC" stroke-width="1.6"/>'
        for c in range(6) for r in range(3))
    return f'''<svg viewBox="0 0 900 260" xmlns="http://www.w3.org/2000/svg" font-family="JetBrains Mono,monospace">
<rect width="900" height="260" fill="#111009"/>
<text x="20" y="28" fill="#EDE8DC" font-family="Rajdhani,sans-serif" font-weight="700" font-size="17">10 · Diagonal arterial  (schematic — needs &gt;4 obs approach slots)</text>
{grid}{circles}
<line x1="120" y1="200" x2="720" y2="60" stroke="#E8C068" stroke-width="5" marker-end="url(#d)"/>
<defs><marker id="d" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 z" fill="#E8C068"/></marker></defs>
<circle cx="360" cy="130" r="10" fill="none" stroke="#FF5E3A" stroke-width="2"/>
<text x="380" y="230" fill="#6A6358" font-size="10">a one-way diagonal makes 5- and 6-leg nodes (circled) — more than the 4 N/E/S/W obs slots</text>
</svg>'''


DESIGNED = [
    (schem_superstreet(),
     "The textbook coordination case: cross-street through and left movements are "
     "banned, so that traffic turns right onto the arterial, runs to a downstream "
     "median <b>U-turn</b> and comes back. One high-conflict crossing becomes a chain "
     "of simple 2-phase ones — every U-turn signal must be offset-timed to the mainline "
     "platoon. Needs hand-authored phases and U-turn geometry the auto-derivation can't "
     "produce.  ▲ coord · 🔴 build"),
    (schem_stagger(),
     "Cross streets meet the one-way pair <b>offset</b>, so a through trip jogs "
     "left-then-right across a short block (two staggered T-junctions). The offsets "
     "that make a green wave work here are non-obvious — a good stress test for a "
     "learned offset vs a naive one. Needs custom offset node geometry.  ● coord · 🟡 build"),
]


def main():
    parts = ['''<!DOCTYPE html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Signal — Complex Scenarios</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Rajdhani:wght@600;700&family=Inter:wght@400;500&family=JetBrains+Mono:wght@400;700&display=swap" rel="stylesheet">
<style>
:root{--void:#111009;--surface:#1E1C16;--raised:#2C2A22;--border:#3D3A30;--tbright:#EDE8DC;--tprimary:#C8C0AE;--tsecondary:#9A9484;--tmuted:#6A6358;--amberb:#E8C068;--steel:#9AAABB;--coral:#FF5E3A;--sage:#7D9A6A;}
body{background:var(--void);color:var(--tprimary);font-family:Inter,system-ui,sans-serif;margin:0;padding:40px 20px;line-height:1.55}
main{max-width:940px;margin:0 auto}
h1,h2{font-family:Rajdhani,Inter,sans-serif;color:var(--tbright);letter-spacing:.02em;text-transform:uppercase}
h1{font-size:30px;margin:0 0 4px}
h2{font-size:19px;margin:8px 0 2px}
.tag{font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--tmuted)}
.chip{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--amberb);margin:2px 0 8px}
p{margin:8px 0}
b{color:var(--tbright)} i{color:var(--tsecondary)}
.card{background:var(--surface);border:1px solid var(--border);border-radius:6px;padding:6px 10px;margin:10px 0}
svg{display:block;margin:0 auto;max-width:100%;height:auto}
.key{background:var(--surface);border:1px solid var(--border);border-radius:6px;padding:14px 18px;margin:16px 0}
.key b{color:var(--amberb)}
.k{font-family:'JetBrains Mono',monospace;font-size:13px;margin:3px 0}
.hdr{border-bottom:1px solid var(--border);padding-bottom:5px;margin-top:40px}
.why{border-left:3px solid var(--amberb);padding-left:12px;margin:10px 0}
.why b{color:var(--amberb)}
</style></head><body><main>
<h1>Signal — Complex City Scenarios</h1>
<div class="tag">orbitope · one-way couplets · turn restrictions · perpendicular thoroughfares · sep 2026 · maps rendered from the live sim</div>

<div class="key">
<p style="margin-top:0"><b>How to read every map.</b> Each is drawn from the real authored network — the model, not a sketch.</p>
<div class="k">━━ <span style="color:var(--amberb)">thick amber</span> = fast <b>arterial / thoroughfare</b> &nbsp;·&nbsp; ── <span style="color:var(--steel)">thin steel</span> = ordinary <b>side street</b></div>
<div class="k">──▶ <b>arrowhead</b> = a <b>one-way</b> street (two-way streets have no arrow) &nbsp;·&nbsp; ── <span style="color:var(--coral)">coral</span> = a <b>right-turn-only</b> lane</div>
<div class="k">● <b>signal</b> (a learning agent) &nbsp;·&nbsp; <span style="color:var(--coral)">▪</span> <b>all-way stop</b> &nbsp;·&nbsp; <span style="color:var(--sage)">▲</span> <b>yield</b> &nbsp;·&nbsp; <span style="color:var(--tmuted)">▪</span> <b>boundary</b> (traffic enters/leaves)</div>
<div class="k"><span style="color:var(--amberb)">▨</span> faint <b>amber band</b> = a heavy origin→destination <b>demand flow</b></div>
<p style="margin-bottom:0"><b>coord</b> rating = how much a neighbour-aware policy should beat a blind one (▲ large · ● moderate). <b>build</b> = 🟢 live here · 🟡 needs care · 🔴 needs a schema/phase extension.</p>
</div>

<p>Turns are fully modelled: each lane carries a turn mask, movements are classified
geometrically, the router only ever routes through legal turns, and phases derive from
the surviving movements. "No left off the arterial" and "right-in/right-out only" are
both just masks. The eight scenarios below are <b>live sim levels</b> (their maps come
straight from <code>--dump</code>) — including the diagonal, which is live only because
we grew the observation schema for it; the last two need further schema/phase work and
are drawn as schematics.</p>
''']

    for svg_name, title, chip, how, why in LIVE:
        parts.append(f'<h2 class="hdr">{title}</h2><div class="chip">{chip}</div>')
        parts.append(f'<p>{how}</p>')
        parts.append(f'<div class="card">{svg(svg_name)}</div>')
        parts.append(f'<div class="why"><b>Why it benefits from the shared policy.</b> {why}</div>')

    parts.append('<h2 class="hdr" style="color:var(--coral)">Two that still push past today\'s schema</h2>'
                 '<p>The diagonal (#10, above) is now live — building it grew the schema from 4 to 8 '
                 'octant approach slots. These last two still need new phase/geometry machinery; drawn '
                 'as schematics with the note on each saying what is missing.</p>')
    for schem, txt in DESIGNED:
        parts.append(f'<div class="card">{schem}</div><p>{txt}</p>')

    parts.append('<p class="tag" style="margin-top:34px">designs in <code>training/scenarios.md</code> · '
                 'builders in <code>Signal.Core/{DowntownBuilder,Scenarios}.cs</code> · '
                 'render any level: <code>--dump &lt;name&gt; | tools/render_network.py</code></p>')
    parts.append('</main></body></html>')
    open(OUT, "w").write("\n".join(parts))
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
