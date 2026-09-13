# Signal — Game Plan

Status: draft v1, 2026-09-12. Supersedes the missing `signal-unity-plan.md`
(that plan covered the foundation, M1–M5.5, and predates the switch to Godot).
This document is about the game itself.

The game has two things the article promises and one thing that already
exists: an **editor** where you build the streets yourself, a set of **casual
puzzles** where you fix a jammed junction with a small toolbox, and the
existing **tactical** mode where you tap approaches for green and race an AI.

## 0. Where we are

The Godot project (`godot/`, Godot 4.6 .NET, ~600 lines) is milestone M2: a
playable slice, not a game. What it already does, and what generalizes for free:

| Piece | State | Generalizes? |
|---|---|---|
| `SimRunner` — fixed 10 Hz sim, render interpolation, lockstep MaxPressure ghost, tap → phase request | done | yes, any `LevelDef` |
| `NetworkView` — draws any network: lane bundles, stop bars per approach, spillback pulse, click-to-approach pick | done | **yes, already generic** |
| `VehicleView` — all vehicles in one MultiMesh draw (4096 cap), colour by wait | done | yes |
| `Orbitope` design tokens + fonts | done | yes |
| `HeadlessSmoke` CI gate (Core runs in Godot's runtime, determinism, lockstep) | done | keep as the gate |
| `Main` — the scene | hardcodes one four-way, fixed camera, HUD text | **no**, this is the part to replace |
| AI in engine | MaxPressure only; the trained RL policy is not in the engine | old M3, not done |

So multi-intersection rendering is not the work. The work is: a level player
with a camera, the puzzle loop, the editor, and getting the trained policy in.

## 1. What the game is

You are handed a piece of a city that is jammed, and a small budget of changes.
Swap a stop sign for a signal, make a street one-way, add a turn bay, ban left
turns, retime a light. Run the traffic. Did it clear? The lights themselves are
driven by an AI, so you are designing the infrastructure, not playing the
lights in real time. That is what makes it casual: build, run, watch, tweak.

Three modes on one engine:

- **Puzzles.** Hand-authored jams with an objective, a toolbox, and a budget.
  Stars for solving under par. The casual product.
- **Editor / sandbox.** Build any network, paint demand, run it, read the
  metrics, race the AI. Also the tool the puzzles are authored with.
- **Tactical.** The existing slice: tap approaches for green against the ghost.
  An optional mode on any level, and the "hard mode" of a puzzle.

Design pillar: **every puzzle's trick is a real traffic effect the sim actually
reproduces.** We have already measured them (section 5). That is the honest
angle and the thing no other traffic toy has: the solutions are true.

## 2. One data model

This is the decision that keeps the scope sane. The editor and the puzzles
share one representation, so the toolbox is the editor with tools switched off.

- The editor edits a **`LevelDef`** (`NetworkDef` + `DemandDef`). It already
  round-trips to JSON (`levels/*.json`, read by `Signal.Headless`).
- A **puzzle** is a `LevelDef` plus a new **`PuzzleDef`**:
  - `objectives[]` — see section 4
  - `toolbox[]` — which edit ops are allowed
  - `budget` — a money total; every tool has a price (a roundabout costs more
    than a stop sign), so the tradeoff is real
  - `par` — the budget the star rating is scored against
  - `seeds[]` — evaluation seeds (never score on one seed; we learned that)
  - `intro`, `hint` — text
- **`BuildVariant`** (per-node control-type overrides) is the existing
  "buildable configuration" primitive; `Simulation(level, seed, build)` and
  `signal-headless --build` already consume it. Generalize it into an **edit-op
  list** that also covers link edits, so a player's solution is a small,
  serializable diff on top of the authored level. That diff is what gets
  saved, shared, and scored.

The puzzle toolbox is therefore not a second system. Build the editor ops once;
puzzles expose a subset.

## 3. The toolbox

Every tool maps to a Core primitive that already exists. Status is honest.

| Player tool | Core primitive | Status |
|---|---|---|
| Change how a junction is controlled: signal, all-way stop, two-way stop, yield, none | `ControlType` via `BuildVariant` | exists |
| Convert a junction to a roundabout | network transform: a ring of `YieldEntry` + `Uncontrolled` nodes (`levels/fourway-roundabout.json` is the template) | builder exists; needs a "macro" op |
| Make a street one-way | remove the reverse link; the router honors it | exists (corridor, couplet) |
| Ban a turn: no left, right-in/right-out | `TurnMask` per lane-link | exists (RIRO, diverge) |
| Add a turn bay or a through lane | parallel lane-link (`LaneBuilder`) | exists |
| Retime a signal: cycle length, green split | `FixedTimePolicy` parameters | exists |
| Hand the lights to the AI | `MaxPressurePolicy` now; the trained policy after section 7 | partial |
| (authoring only) demand: flows, rush-hour curves, tidal reversal | `DemandDef`, `RateCurve`, `Rush()` | exists |

Signal phase plans should be **auto-generated** (NS/EW through, with or
without protected lefts, from the node's geometry). Casual players never author
phases; the phase validator in `Network.cs` (fails at load) is the safety net
if the editor produces something illegal.

## 4. Objectives and scoring

All objectives read straight off `Metrics` and the sim:

- **Average wait ≤ X s** — `LiveAvgWait` (counts every vehicle that ever existed,
  including those held at entry, so starving an approach cannot game it)
- **Clear N cars by time T** — `Completed`, `ThroughputPerMin`
- **No spillback** — `SpillbackEvents == 0`
- **Nobody waits more than Y s** — max wait / starved fraction. This is the
  fairness objective; it exists because we measured that "hold the arterial
  green forever" wins on average wait by starving every side street.
- **Under budget** — stars: 3 for solving at or under par, 2 for over, 1 for
  solved at all.

Score over `seeds[]` (3–5), report the mean, and require the objective on every
seed. Determinism (`StateHash`) means a solution replays identically, which
also makes solutions shareable as a diff.

## 5. Puzzle archetypes for the first pack

Each one is a measured result from this project, turned into a level. The
"source" column is where the number came from.

| # | Puzzle | The trick the player discovers | Tool | Source |
|---|---|---|---|---|
| 1 | Stop or signal? (light traffic) | at 8 veh/min the all-way stop beats the signal | control swap | M5.5 table |
| 2 | Stop or signal? (heavy) | at 45 veh/min the stop sign collapses; the signal wins | control swap | M5.5 table |
| 3 | The roundabout | balanced demand: roundabout wins by a mile | roundabout macro | M5.5 table |
| 4 | The roundabout trap | one dominant flow monopolizes the circle; it degrades 1.4–1.6× | swap back to a signal | M5.5 table |
| 5 | The left-turn bay | lefts block the through lane; a bay + protected phase fixes it | add bay | article, phase figure |
| 6 | Two lights, one short block | spillback between close signals | one-way, or lane | article, coupling |
| 7 | The couplet | convert a two-way pair to one-way opposites; the green wave appears | one-way | couplet +20 % |
| 8 | Right-in, right-out | side streets turning left across an arterial jam it; ban the lefts | turn mask | RIRO scenario |
| 9 | Rush hour | a fix that works off-peak fails at the peak; needs a second change | any | corridor-rush, tidal |
| 10 | The starved side street | the "obvious" fix hits the wait target by starving one approach; fairness objective rejects it | retime / control | hold baseline lesson |
| 11 | Hand it to the AI | your best geometry vs. letting the AI run the lights | AI tool | article result |

Proposed structure, about 24 puzzles in three worlds, each world unlocking on
stars:

- **World 1 — One junction** (8): archetypes 1–5 and variants. Teaches every tool.
- **World 2 — Two lights** (8): 6, 7, and coordination. Introduces spillback.
- **World 3 — The corridor** (8): 8, 9, 10, 11 on the 5×3 corridor and the
  couplet grid. The real networks from the article.

The scenario builders (`sc-couplet`, `sc-onewaypair`, `sc-riro`, `sc-diverge`,
`sc-tidal`, `sc-platoon`, `sc-mixed`) are starting material for World 3; most of
it is authored already, it just needs a broken starting state and an objective.

## 6. The editor

Grid-snapped, not free-form. It matches how every builder in Core lays out
networks, it keeps junction geometry legal (four cardinal approaches, so phase
auto-generation always works), and it is what a casual player can actually use.

MVP:
- place / delete junctions on the grid; boundary (entry/exit) nodes at the edge
- drag between junctions to connect: two-way by default, toggle one-way
- per street: lanes (1–2), turn bay, turn mask
- per junction: control type picker, roundabout macro, signal retiming
- demand: paint origin→destination flows between boundary nodes from presets
  (light / commute / balanced), rush-hour toggle
- validate on run (phase validator, routability: every flow must have a path)
- run / pause / reset, 1×/2×/4×, metrics panel, spillback flashes
- save / load `LevelDef` JSON
- **Export as puzzle**: attach objectives, choose the toolbox and budget, set
  par by solving it yourself, write a `PuzzleDef`

Later: undo/redo, copy a scenario builder as a starting template, a "diff"
view that highlights what the player changed from the authored level.

## 7. The AI in the engine (old M3)

The trained shared policy is a 62k-parameter MLP over a 233-float observation.
That does not need ONNX or any runtime dependency:

1. Export the checkpoint's actor weights to a plain JSON/binary file.
2. Add `LearnedPolicy : ISignalPolicy` to `Policies.cs`: a few dense layers
   and an argmax, in C#, deterministic.
3. Build the observation with the existing `AgentView` in `Observations.cs`
   (the engine already has the exact schema the policy was trained on).
4. Gate it in `HeadlessSmoke`: the in-engine policy must reproduce the Python
   evaluator's greedy actions on a fixed level and seed.

Then the trained policy becomes: the default light-driver in puzzles, the
ghost you race in tactical mode, and the "hand it to the AI" tool. Until this
lands, MaxPressure fills all three roles, which is honest and costs nothing.

Risk: the observation schema is v3 (8 octant slots). Keep the layout
header-driven as it is in the Python stack, and never hardcode 233.

## 8. Roadmap

Rough, solo, part-time weeks. Each phase ends at a gate you can actually check.

**P0 — Level player** (1–2 weeks)
Replace the hardcoded scene: load any `LevelDef` JSON, camera fit / pan / zoom,
run / reset / speed, a results panel, keep the smoke gate green.
*Gate:* play `sc-couplet` and the corridor in Godot, tap any approach.

**P1 — Puzzle MVP** (2–3 weeks)
`PuzzleDef`, the edit-op list, four tools (control swap, one-way, turn mask,
retime), objective check over seeds, stars. World 1 authored in code, the way
`Scenarios.cs` levels are.
*Gate:* someone who has not seen the game finishes five World 1 puzzles
without help, and says which one was the most satisfying.

**P2 — AI in engine** (1–2 weeks)
Section 7. AI-driven lights become the default; the ghost is the real policy.
*Gate:* smoke test reproduces the Python evaluator's actions.

**P3 — Editor** (3–4 weeks)
Section 6 MVP, then author Worlds 2 and 3 *with the editor* rather than in
code. That is the dogfood.
*Gate:* build the corridor from an empty grid, export it as a puzzle, and play it.

**P4 — Progression and polish** (2–3 weeks)
Worlds and star gates, a tutorial that is just World 1 with more text, hints,
a replay of the run, sound and juice, settings, and the accessibility pass (the
colour-blind rules from the article's design system already apply: sage/terra
need a second channel).

**P5 — Ship**
Platform decision (section 9), store page, a playtest round, then release.

## 9. The platform problem

**Godot 4.6 C# cannot export to the web.** The .NET runtime does not run in
the browser sandbox; a Web .NET prototype was shown at GodotCon in 2025 but is
not shipped as of September 2026. Casual puzzle games live on the web and
phones, so this is the biggest strategic constraint in the plan.

Options:

- **A. Native first.** Desktop (Windows/macOS/Linux) plus Android and iOS via
  the .NET export. Ship on itch.io and, if it earns it, Steam. Zero rework.
- **B. Port Core to GDScript for a web build.** Core is a couple of thousand
  deterministic lines, so this is feasible, but it is a second implementation
  to keep in sync. If we do it, differential-test it against C# with
  `StateHash` the way the Python stack tests the env: same seed, same hash.
- **C. Wait for Godot's Web .NET.** Free if it lands, unknowable when.
- **D. Web "lite".** The article already replays recorded rollouts in the
  browser with no engine. Shareable puzzle *results* (a link that replays your
  solution) could use that, without porting the game.

Decided (section 10): **A, desktop only** for v1. B is the web fallback to
revisit after release, not before.

## 10. Decisions (2026-09-12)

1. **Turn-based.** Build, then run at speed and watch. Tapping is only the
   tactical mode. Casual means no twitch.
2. **Budget is money with per-tool prices.** A roundabout costs more than a
   stop sign; par is a dollar figure, and stars score against it.
3. **Grid-snapped editor.** Free-form is out of scope.
4. **MaxPressure drives the lights in P1; the trained policy replaces it in
   P2**, and the difference is surfaced as a feature ("hire the better AI").
5. **v1 is three worlds, about 24 puzzles, plus the editor.**
6. **Ship native, desktop only** (Windows, macOS, Linux). No mobile and no web
   in v1; the GDScript port (section 9, option B) is a post-release question.

## 11. Non-goals for v1

No multiplayer, no real-world map import, no city-economy layer, no 3D, no
pedestrians or transit. One good loop, three worlds, an editor that works.
