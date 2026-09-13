# Signal — proposed complex scenarios (brainstorm)

Ten networks that push past the uniform grid into real city structure — one-way
couplets, perpendicular thoroughfares, and turn restrictions — chosen so each
isolates a *different* coordination phenomenon. The prior finding drives the
selection: **coordination's value tracks how much exploitable structure the
traffic has**, so these deliberately manufacture structure (progression bands,
forced detours, platoons) that a neighbour-aware shared policy can exploit and a
blind one cannot.

Turn machinery is real: per-lane-link `TurnMask`, geometric classification,
router honours it, phases derive from the surviving movements. "Right-only from
the arterial" = `turns=Right`; "no lefts off the arterial" = `turns=Through|Right`.

Legend: **Coord** = expected shared-vs-independent gap (▲ large, ● moderate,
○ small). **Build** = effort/risk with today's primitives (🟢 straightforward,
🟡 needs care, 🔴 needs a schema/phase extension).

---

### 1. Downtown one-way couplet grid  ▲  🟢
Manhattan CBD: every street one-way, alternating direction on parallel streets
(a "couplet") on **both** axes — E-bound / W-bound streets interleaved, N/S
likewise. **Turns:** lefts banned everywhere (`Through|Right`) — the real rule
that keeps one-ways flowing; only through + right. **Demand:** even, but the
one-way structure forces long directional runs. **Why:** pure green-wave
progression — the entire payoff is phase *offsets* between adjacent signals, the
canonical thing a shared policy learns and an independent one structurally can't.
This is the cleanest coordination-rich test.

### 2. One-way pair + perpendicular thoroughfares  ▲  🟢  *(your ask)*
Two adjacent parallel one-ways running **opposite** directions (a couplet),
crossed by 3 heavy two-way **thoroughfares**. **Turns:** right-only *from* the
thoroughfares onto the one-ways (`turns=Right` on those approaches) — feed the
couplet without blocking the box; lefts from the one-ways banned. **Demand:**
heavy directional on the couplet + heavy two-way on the thoroughfares, so they
compete. **Why:** the signals must progress the couplet *and* platoon-serve the
thoroughfare crossings — two coupled rhythms. Independent agents can't phase-lock
the couplet.

### 3. Access-managed arterial — right-in / right-out  ●  🟢
A fast two-way arterial; every side street is **RIRO** — `turns=Right` both on
entry to and exit from the arterial, no through, no left. **Demand:** heavy
arterial + side-street trips that must now turn right and **route around the
block** (the turn-aware router does this automatically). **Why:** concentrates
load onto specific right-turn movements and downstream U-turn-via-block paths;
tests whether the policy learns to serve the manufactured turn pockets. Also a
clean demonstration that banning turns *reshapes routing*, not just phases.

### 4. Superstreet / RCUT corridor  ▲  🔴
Cross-street **through and left banned** at the main junctions; that traffic
turns right onto the arterial, runs to a downstream **median U-turn** signal, and
comes back. Splits one high-conflict crossing into a chain of simple 2-phase
ones. **Turns:** mainline `Through|Right`; cross `Right` only; dedicated U-turn
nodes. **Why:** the textbook case where coordination *is* the design — every
U-turn signal must be offset-timed against the mainline platoon. Highest coord
payoff, but needs **hand-authored phases** and U-turn geometry (auto-derivation
won't produce these).

### 5. Diverging one-way couplet off a two-way spine  ●  🟡
A two-way arterial that **splits** into a one-way pair around a plaza/block and
**rejoins** (wishbone). **Turns:** channelised at the split/merge (`Right` /
`Through` masks force the divide). **Why:** merge/diverge metering — the join
signal must not starve or overfill either leg; tests coordination across an
asymmetric topology rather than a regular grid.

### 6. Reversible-flow rush couplet  ●  🟢
A one-way pair carrying a strong **tidal** load: AM peak dominates one direction,
PM the other, via `RateCurve`s (ramp/peak/drain, opposite phases). **Turns:**
lefts banned off the couplet. **Why:** non-stationary *and* directional — the
policy must shift green split toward the dominant direction over the episode and
back. Extends the corridor-rush result with structure a fixed-time plan can't
track.

### 7. Freeway off-ramp platoon surge  ●  🟡
A boundary that dumps **platoons** (short high-rate `RateCurve` pulses, not
smooth Poisson) from a metered ramp onto a downtown one-way grid. **Turns:**
right-only off the ramp. **Why:** bursty, correlated arrivals — the signal must
flush a platoon then recover, and downstream signals must catch the platoon
(progression). Tests robustness to non-Poisson demand, which every scenario so
far avoided.

### 8. Heterogeneous-control district  ▲  🟢
A signalized one-way grid with a thoroughfare, but **not every node is a signal**
— some minor crossings are all-way **stops** or **yield**/uncontrolled. **Turns:**
lefts banned on the arterial. **Why:** the obs already carries each neighbour's
**control type**; this is the only scenario that exercises it. The shared policy
can learn to lean on a stop-controlled neighbour differently than a signalized
one — coordination *across control types*, which an independent agent is blind to.

### 9. Staggered-T couplet (old-city stagger)  ●  🟡
Cross streets are **offset** — a "through" trip on the couplet must jog left-then
-right across a short block (two staggered T-junctions). **Turns:** the stagger
forces the jog via masks. **Why:** progression through offset nodes with a built-
in mid-block delay — the offsets that make a green wave work are non-obvious here,
a good stress test for the learned offset vs a naive one.

### 10. Diagonal arterial over a grid (Broadway)  ▲  🔴
A one-way **diagonal** cutting across a rectangular one-way grid, creating 5- and
6-leg offset intersections with acute-angle turns. **Turns:** acute lefts banned
(`Right`/`Through` only) off the diagonal. **Why:** the richest conflict/priority
structure of the set — irregular nodes where coordination is hardest. **Requires
an obs-schema extension** (>4 approach slots) and generalised phase derivation;
the biggest build, and a natural driver for the attention/GNN policy the ladder
points toward.

---

## Reading the set

- **Build first (coordination-rich, low-risk):** #1 couplet grid, #2 one-way pair
  + thoroughfares, #8 heterogeneous control. All 🟢, all ▲/● gap expected, all
  reuse the auto phase derivation. #2 is your exact ask and the best single
  headline.
- **Second wave (needs care):** #3 RIRO, #5 wishbone, #6 tidal, #7 platoons,
  #9 stagger.
- **Research-grade (schema/phase work):** #4 superstreet, #10 diagonal — these
  are where the current obs/phase machinery genuinely has to grow, and where a
  GNN policy earns its place.

Common build work these share: a `DowntownBuilder` that lays a grid with
per-street direction + per-approach `TurnMask`, and a demand author that places
directional couplet flows, thoroughfare flows, and (for #6/#7) shaped rate
curves. #4/#10 additionally need hand-authored phases and (for #10) an obs bump.
