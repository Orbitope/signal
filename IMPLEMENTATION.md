# Signal — Implemented Core

This is the working implementation of **M1 + M5.5 (Core side)** from `signal-unity-plan.md`,
plus the Unity adapter reference for M2/M3. Everything under `Signal.Core/` compiles as
`netstandard2.1` with **zero dependencies** — it drops into Unity 6 behind the included
`noEngineReferences` asmdef unchanged.

## Repo map

```
Signal.Core/          the simulation (netstandard2.1, zero deps — Unity-ready)
  Rng.cs              xoshiro256** — deterministic across platforms/runtimes
  Defs.cs             authored data: NetworkDef/DemandDef/LevelDef/BuildVariant
                      (public fields → works with STJ IncludeFields AND Unity serialization)
  Network.cs          runtime graph, movement derivation, geometric conflict matrix,
                      phase validator (fails at load, never at runtime)
  Vehicle.cs          Vehicle, SimConfig (DT=0.1s), IDM
  Controls.cs         IIntersectionControl: Signalized / AllWayStop / TwoWayStop /
                      YieldEntry, + the shared GapAcceptance primitive
  Policies.cs         ISignalPolicy: FixedTime / Greedy / MaxPressure / External
  Demand.cs           Poisson thinning vs rate curves, entry queues, Dijkstra router
  Simulation.cs       the step loop, movement resolution, spillback, metrics, StateHash
  NetworkBuilder.cs   canonical four-way (every control variant, same boundary ids),
                      arterial(k), roundabout — programmatic benchmark networks
Signal.Headless/      CLI: bench / run / hash / export, JSON levels, --build variants
Signal.Tests/         12 tests incl. every plan gate (custom runner; use xunit in the
                      real repo — NuGet was unavailable in this build environment)
levels/               exported JSON for all built-in levels
UnityReference/       DOES NOT compile here — exact code to carry into Unity:
  SignalAgent.cs      ML-Agents adapter: 73-float padded obs schema (documented in
                      the header), action masking, pressure reward
  SimRunner.cs        MonoBehaviour: fixed-tick stepping, render interpolation, ghost
  *.asmdef            Core with noEngineReferences:true, Agents referencing ML-Agents
```

## Run it

```bash
cd Signal.Tests    && dotnet run -c Release        # all 12 gates
cd Signal.Headless && dotnet run -c Release -- bench --builtin arterial3 --seeds 5
                      dotnet run -c Release -- hash --builtin fourway-signal --steps 20000
                      dotnet run -c Release -- export   # write levels/*.json
```

## Verified results (5 seeds, 600 s each)

| scenario | fixed | greedy | MaxPressure |
|---|---|---|---|
| four-way, symmetric 24/min | 55.8 s | 61.1 s | **53.6 s** |
| four-way, asymmetric 70/30 @20/min | 11.0 s | — | **8.7 s** |
| arterial-3 | 207.9 s | 182.0 s | **174.0 s** |

| control type (same demand, same boundary ids) | low 8/min | high 45/min | balanced 24/min | dominant-flow 24/min |
|---|---|---|---|---|
| fixed signal | 7.5 s | **156 s** | — | — |
| all-way stop | **5.9 s** | 177 s | — | — |
| roundabout | **0.7 s** | — | **12.3 s** | 17.0 s (1.4–1.6×) |

That table **is** the M5.5 gate and the §5.5 budget-strategy claim, produced by the sim
rather than asserted: stop signs win at low volume, signals win under load, roundabouts
dominate balanced demand and degrade when one flow monopolizes the circle.

Other gates: determinism hash identical over 20k steps; min-green/yellow/all-red hold
against an adversarial flapping policy; vehicle conservation exact over 30k steps;
spillback fires on a short-link flood; **~30k steps/sec** single-threaded (gate: 10k).

## Three bugs the implementation surfaced (now tests)

1. **Mutual permissive-left deadlock.** Two opposing lefts each saw the other stopped
   at the line and yielded forever — in single-lane, that freezes both approaches and
   gridlocks the map. Fix: a stopped vehicle near the line blocks only if *its intended
   movement* conflicts with yours (`GapAcceptance`). Lesson: yield rules need intent,
   not just presence.
2. **MaxPressure thrashing.** Deciding every 0.5 s with a 4 s yellow+all-red cost made
   the "optimal" policy lose to naive fixed timing. Fix: 5 s decision interval +
   pressure stickiness ≈ switching cost. Lesson for M3: the RL agent's decision
   interval is a first-order hyperparameter, and *symmetric demand is fixed-time's
   best case* — benchmark adaptive control on asymmetric demand.
3. **False spillbacks.** Entry-gap contention during normal close-following was counted
   as spillback and hard-stopped vehicles at the line. Fix: spillback = downstream link
   *actually full*; otherwise smooth IDM continuity through the node following the next
   link's tail vehicle. Waits improved everywhere once vehicles stopped stutter-stopping.

## Deltas from the plan doc

- Adaptive policies default to `DecisionInterval = 5 s` (plan said 0.5 s) — see bug 2.
- JSON I/O lives in consumers (Headless uses System.Text.Json with `IncludeFields`),
  not Core, keeping Core at literally zero references. Unity side uses its own
  serialization over the same `Defs.cs` types.
- `HasEntrySpace` is used for spawn/transfer admission only; front-vehicle dynamics use
  the full-link check + tail-following (bug 3).

## Porting into Unity (M2 start)

1. Copy `Signal.Core/*.cs` into `Assets/Signal/Core/` next to the provided asmdef.
2. `SimRunner.Load(level, seed)` → subscribe pools/VFX to the three events.
3. `PlayerTapPolicy` is `ExternalPolicy` — wire UI taps to `RequestPhase`.
4. Keep `Signal.Headless` building in CI against the same files
   (`<Compile Include="../Assets/Signal/Core/**/*.cs">`): it is the guard that keeps
   UnityEngine out of Core.
