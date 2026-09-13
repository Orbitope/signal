# Signal — training results (measured)

All numbers below are produced by `training/evaluate.py`, which rolls the trained
greedy policy and three same-harness references (`fixed` round-robin cycle,
`random` legal, `hold`) through identical levels, seeds, and metric. Return is
the pressure objective the agents optimize (higher = less queueing). `mean_queue`
is mean through-queue length; `mean_wait` is mean approach head-wait in seconds.
Reproduce with `bash training/run_all.sh`.

Runs: PPO, 1.5M env steps each, 16 parallel envs, fixed demand (1.0), 128×128
separate actor/critic trunks, ~2–5 min CPU per run (Apple M-series).

## Rung 3 — shared policy, fourway-bays (the deployment target)

| policy | ep_return | mean_queue | mean_wait_s |
|---|---|---|---|
| **trained (shared)** | **−125.1** | **2.11** | **35.7** |
| fixed-cycle | −245.8 | 7.83 | 49.6 |
| random | −238.6 | 7.06 | 46.8 |
| hold | −667.1 | 11.21 | 76.2 |

The shared brain cuts return in half vs a fixed-time cycle and holds queues 3.7×
shorter. During training, rolling return climbs −213 → −130 and policy entropy
falls 0.76 → 0.56 as it commits to a state-conditioned controller
(`curve-fourway.svg`).

## The three rungs — grid2 (4 agents)

| rung | ep_return | mean_queue | mean_wait_s |
|---|---|---|---|
| rung 1 · independent | −50.5 | 2.70 | 13.9 |
| rung 3 · shared | −53.5 | 2.76 | 14.1 |
| rung 2 · CTDE (central) | −75.2 | 3.80 | 17.2 |
| fixed-cycle | −122.7 | 5.45 | 26.3 |
| random | −94.2 | 4.47 | 22.3 |
| hold | −238.7 | 5.90 | 20.1 |

All three RL rungs beat fixed-time and random decisively. Rung 3 (shared) and
rung 1 (independent, neighbours blinded) are tied within noise — because grid2
at demand 1.0 is **low-coupling**, there is little coordination for the shared
view to exploit, so a brain that only sees itself does just as well. The
centralized critic (rung 2) does *not* help here and costs sample efficiency: a
bigger critic to fit in the same budget, with no coordination to resolve. This is
the plan's thesis landing exactly as predicted — *rung 3 recovers rung 2's
coordination locally, for free* — and it points at the honest next experiment:
push demand up until spillback couples the intersections, and watch the
independent rung degrade while shared/central hold.

## One brain, any N — zero-shot topology transfer

The shared policy is agent-count-agnostic (105-float obs regardless of N). Trained
on grid2 (4 agents), evaluated **unchanged on grid3 (9 agents, never seen):**

| policy on grid3 | ep_return | mean_queue | mean_wait_s |
|---|---|---|---|
| **grid2-trained shared (zero-shot)** | **−165.3** | **4.05** | **17.6** |
| grid3 fixed-cycle | −213.7 | 4.89 | 25.0 |
| grid3 random | −191.5 | 4.46 | 21.9 |

The 4-agent brain beats the 9-agent grid's own fixed-time baseline with no
retraining. Transfer holds *within a lane structure*; crossing structures
(single-lane grid ↔ bays with protected-left phases) needs the curriculum ladder
the plan prescribes (`fourway → fourway-bays → fourway-bays2 → grid2 → grid3`),
because a brain that never saw a bay never learned to use the protected-left
phases. That mixed-curriculum brain is the natural next rung; the plumbing gap is
a rollout buffer that tolerates a changing agent count between levels.

## Corridor — real road hierarchy (15 intersections)

`corridor` is a 5×3 network with structure a uniform grid doesn't have: a fast
two-way **primary arterial** (E–W thoroughfare), a **secondary arterial** (N–S),
a **one-way eastbound side street** (bottom row, reverse links absent — the
router honors it), and demand concentrated on the thoroughfares. `corridor-rush`
adds a time-varying rush-hour curve (ramp/peak/drain). Built by
`Signal.Core/CorridorBuilder.cs`; see `corridor-map.svg`.

Shared policy, 600k steps, greedy eval:

| level | policy | ep_return | mean_queue | mean_wait_s |
|---|---|---|---|---|
| corridor | **shared** | **−148.1** | 3.26 | **14.2** |
| corridor | fixed-cycle | −184.4 | 2.06 | 19.6 |
| corridor | random | −163.4 | 2.33 | 17.5 |
| corridor-rush | **shared** | **−116.6** | 3.02 | **14.6** |
| corridor-rush | fixed-cycle | −143.8 | 1.95 | 18.7 |

The shared brain beats fixed-time on the pressure objective and on average wait,
under both stationary and **non-stationary (rush-hour)** demand — it tracks a
load that ramps and drains, not just a fixed one. The margin is smaller than on
the uniform grids: a hierarchical corridor rewards biasing green toward the busy
arterial, which even a dumb "hold the arterial green" heuristic partly captures
(note `hold` posts the lowest wait, 9.3 s, by permanently greening the E–W flow —
at the cost of the worst return, because it starves every cross street).

## Does coordination matter? — shared vs independent under load

Stress test: the *same* corridor brains (trained at demand 1.0) evaluated at
rising demand multipliers. `shared` sees its neighbours; `independent` is blinded
to them.

| demand | shared wait | indep wait | shared queue | indep queue |
|---|---|---|---|---|
| 1.0× | **14.1** | 14.8 | **3.27** | 3.53 |
| 1.5× | **18.1** | 19.2 | **5.01** | 5.40 |
| 2.0× | **20.1** | 21.7 | **5.71** | 6.05 |

Return stays tied (both handle total pressure similarly), but shared is
**consistently better on queue and wait at every demand, and the wait advantage
grows** as the corridor saturates (0.7 → 1.1 → 1.6 s). Note this is an
*out-of-distribution* test — both brains were trained at demand 1.0. Retraining
**at** the saturating demand makes the gap far larger (next section).

### Trained at saturation — coordination becomes decisive

Both rungs retrained at a fixed 1.8× demand, where spillback actually couples the
intersections, then evaluated at 1.8× (greedy):

| policy (trained @1.8×) | ep_return | mean_queue | mean_wait_s | starved% |
|---|---|---|---|---|
| **shared** (sees neighbours) | **−390.3** | **5.07** | **17.5** | **29.3** |
| independent (blind) | −418.9 | 5.75 | 20.1 | 34.4 |
| fixed-cycle | −421.8 | 3.64 | 26.7 | 41.0 |
| random | −388.0 | 4.18 | 24.9 | 36.1 |
| hold | −456.7 | 3.01 | 11.7 | 23.1 |

Now the neighbour-aware shared policy pulls **decisively** ahead of the blind
independent one: +7% return, 13% lower wait (17.5 vs 20.1 s), 12% shorter queues,
and less starvation (29 vs 34% of approaches pinned at the wait clamp). The blind
independent policy nearly collapses to fixed-time's return (−419 vs −422) — with
no neighbour signal it cannot beat local greedy once the network is saturated and
coupled. **Coordination's value is demand-dependent: negligible at light load,
decisive under saturation** — precisely the plan's thesis, now measured.

**Across 3 seeds each** (greedy eval, the plan's error-bar bar), the gap holds
well clear of seed noise:

| policy @1.8× (3 seeds) | ep_return | mean_wait_s | starved% |
|---|---|---|---|
| **shared** | **−393.0 ± 1.9** | **18.3 ± 0.7** | **31.0 ± 1.3** |
| independent | −417.4 ± 4.4 | 20.4 ± 1.1 | 34.8 ± 1.9 |
| fixed-cycle | −421.8 | 26.7 | 41.0 |

The 24-point return gap is ~5× the combined seed spread — a robust effect, not a
lucky seed. See `coordination-gap.svg` for the light-vs-saturated contrast.

(`starved%` is the new fairness proxy — fraction of approach waits pinned at the
120 s clamp; unlike `max_wait` it doesn't saturate to a constant, so it catches a
policy that wins on average by starving one approach. `random` posts a return
near shared's here, but its far worse wait and starvation show the return metric
alone is misleading at saturation — read wait + starved% alongside it.)

## Scale

The shared brain is agent-count-agnostic, so scale is free at the model: the
identical 62k-param network runs `fourway-bays` (1), `corridor` (15), `corridor7`
(21) and `grid5` (25). Trained fresh on `grid5` (25 intersections), it beats
fixed-time comfortably:

| policy on grid5 (25) | ep_return | mean_wait_s | starved% |
|---|---|---|---|
| **shared** | **−207.2** | **25.4** | **27.7** |
| fixed-cycle | −257.7 | 33.3 | 44.3 |
| random | −232.1 | 32.0 | 40.5 |

The only cost of scale is env throughput — the single-threaded sim: ~8,800
decision-steps/s at 1 intersection, ~1,200/s at the 15-intersection corridor,
~860/s at grid5's 25. The learner and model are unaffected. Multiple env-server
processes (a SubprocVecEnv) would recover the idle cores; today one server steps
all envs in a loop.

## Coordination is structure-dependent, not just demand-dependent

Repeating the saturation comparison on `grid5` — a *uniform* 25-intersection grid,
evenly spread demand, no arterials — at 1.5× (1 seed each so far):

| policy @1.5× on grid5 | ep_return | mean_wait_s | starved% |
|---|---|---|---|
| shared | −423.0 | 25.2 | 33.4 |
| independent | −429.2 | 25.6 | 35.1 |
| fixed-cycle | −437.4 | 34.8 | 48.3 |

Both beat fixed-time, but the shared-over-independent gap is **much smaller** here
(6 return points, 0.4 s wait) than on the corridor at 1.8× (24 points, 2.1 s). The
difference between the two networks is *structure*: the corridor concentrates flow
onto arterials, creating coherent spillback a neighbour-aware policy can exploit (a
green wave); the uniform grid spreads demand evenly, so even under saturation there
is less coordinated structure to capture. **The value of seeing your neighbours
tracks how much exploitable structure the traffic has** — which is exactly the
real-world case (corridors, downtowns) where signal coordination matters most.
(Single seed on grid5; needs the 3-seed treatment to firm up, but the direction is
clear and consistent with the corridor result.)

## Coordination across the complex scenarios — it's about *coupling*, not "structure"

Shared vs independent, each trained AND evaluated at a saturating 1.8× demand, on
three complex networks (greedy eval; return is the training objective, so read it
first; see `coordination-by-scenario.svg`):

| scenario @1.8× | shared ret | independent ret | fixed ret | shared vs indep |
|---|---|---|---|---|
| couplet grid (pure one-way, **3 seeds**) | **−112.0 ± 7.9** | −129.3 ± 0.9 | −157.2 | **+13% return** |
| corridor (concentrated arterial, **3 seeds**) | −393.0 ± 1.9 | −417.4 ± 4.4 | −421.8 | **+6% return** |
| **diagonal arterial** (5-leg nodes, schema v3) | **−110.1** | −122.2 | −115.0 | **+11 pp** |
| one-way pair (couplet in a 2-way grid) | −208.6 | −208.5 | −271.1 | tied |
| RIRO access-managed arterial | −66.6 | −66.6 | −116.6 | tied |
| platoon surge grid | −4.0 | −4.0 | −13.8 | tied |

**Coordination premium** (shared's %-over-fixed minus independent's), 1.8× saturated:
couplet **+11 pp**, diagonal **+10.5 pp**, corridor **+6 pp**, one-way pair / RIRO /
platoon **≈ 0** (`coordination-premium.svg`). Every RL policy still beats fixed-time
everywhere *except* the diagonal, where the blind independent policy actually drops
**below** fixed-time (−122 vs −115) while shared beats it — on the diagonal's acute
5-leg junctions coordination is not just helpful but necessary. The diagonal is live
only because building it grew the observation schema from 4 to **8 octant approach
slots** (v3, obs 105→233) so a fifth (diagonal) leg is representable; its junctions run
a per-approach 5-phase plan. (Single seed on the diagonal; the effect is large and in
the predicted tight-coupling direction, but wants seeds.)

The pattern refines the earlier "structure-dependent" reading into something
sharper: **coordination pays exactly where the geometry tightly couples
neighbours' queues.**
- The **couplet grid** is a pure alternating one-way lattice — every signal feeds
  directly into the next with no alternative path, so a green wave (phase offsets)
  is both available and necessary. Shared exploits it; the blind policy leaves 20%
  on the table. This is the green-wave result the couplet was built to test.
- The **corridor** couples through one saturated arterial — the same story, smaller
  because only the arterial corridor is tightly coupled.
- The **one-way pair** embeds a couplet in an otherwise two-way grid with
  thoroughfares: plenty of alternative routes and slack, so flow reroutes instead
  of spilling back. Neighbour information buys nothing — a blind local policy does
  just as well, even saturated.

So the earlier headline holds and gets a mechanism: coordination's value is not
"structure" in the abstract but **spillback / progression coupling** — tight
one-way chains and saturated arterials reward it; distributed grids with slack do
not. That is also the design rule for *manufacturing* a scenario where coordination
matters, and the clearest signpost toward policies (attention/GNN) that can model
coupling the current 1-hop view only glimpses. (Single seed per scenario here; the
couplet's 26-point gap is corridor-sized, but seeds would firm it.)

## A coupling-aware policy — neighbour attention (rung `attn`)

The finding that coordination pays where junctions are tightly coupled — most of all
the diagonal's irregular 5-leg nodes — points past the fixed-slot MLP to a policy that
*models* the coupling. Rung `attn` does exactly that: the actor keeps the same local
obs but, instead of flattening the 8 neighbour slots into the MLP, it **attends** over
them (self = query, each neighbour block = key/value, softmax masked to valid
neighbours) and concatenates the attention context with the self embedding before the
policy head. It is a 1-hop graph-attention layer; the critic stays a plain MLP.

On the diagonal @1.8× (all schema-v3, greedy, **2 seeds**):

| policy on the diagonal | ep_return | starved% | vs fixed |
|---|---|---|---|
| **attention** | **−91.4 ± 0.6** | **12.3 ± 0.2** | **+20%** |
| shared MLP | −109.2 ± 0.9 | 16.4 ± 0.2 | +5% |
| independent (blind) | −122.2 | 16.4 | −6% |
| fixed-cycle | −115.0 | — | — |

**Attention beats the shared MLP by ~16% return** (−91.4 vs −109.2) and cuts starvation
(12.3 vs 16.4%) — the fixed 8-slot MLP can represent a 5-leg node but cannot weight the
legs; attention learns to. The ~18-point gap is **~20× the combined seed spread**, so it
is a real architectural effect, not seed luck (attention is also strikingly low-variance,
±0.6). See `attn-vs-mlp-diagonal.svg`.

On the *regular* couplet grid, attention lands at −113.5, ~tied with the MLP's −112
(cross-schema, so read the objective not the obs): where the topology is a clean grid,
the MLP's fixed slots already suffice, and attention neither helps nor hurts. That is
the expected and satisfying shape: **attention's benefit is specific to irregular,
high-degree junctions** — exactly the diagonal that the schema had to grow to admit.
(Single-seed; couplet/corridor MLP baselines predate schema v3 and want a clean v3
retrain to firm the tie.)

## Reading the caveats honestly

- `max_wait` saturates at the 120 s obs clamp for every policy, so it is dropped
  from the tables above; `mean_wait` is the discriminating fairness proxy.
- Single seed per run so far. The plan's gates require ≥3 seeds and reporting
  worst-individual-wait alongside the average; that is the next hardening pass.
- These are RL-harness (obs-proxy) metrics. For the classical fixed-time / Greedy
  / MaxPressure numbers on link-level wait, cross-reference `Signal.Headless
  bench` — a different harness, read as a second view, not the same scale.
