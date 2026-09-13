# Signal — MARL training stack

The three rungs of `signal-training-plan.md`, trained with one compact PPO over
the existing `Signal.EnvServer`. Capacity is never the constraint here (the
largest brain in the project is a few MB); what this code exercises is the
plan's actual thesis — **parameter sharing + a locally-computable pressure
reward recovers most of the coordination a centralized critic buys, and it
generalizes across topologies.**

```
signal_env.py   VecEnv client + Gate-0 validation suite (already shipped)
policies.py     actor-critic net + the three rungs (shared / independent / central)
ppo.py          the multi-agent PPO core (rollout, GAE, clipped update)
train.py        CLI: train one rung on one level, checkpoint + JSON log
evaluate.py     score a checkpoint vs fixed-cycle / random / hold, same harness
plot.py         standalone-SVG learning curves from the JSON logs
```

## Setup

```bash
python3 -m venv training/.venv
training/.venv/bin/pip install numpy torch
(cd Signal.EnvServer && dotnet build -c Release)     # the env, once
training/.venv/bin/python training/signal_env.py     # Gate 0: plumbing green
```

## The three rungs

| rung | `--rung` | what's shared | what the critic sees | params (128×128) |
|---|---|---|---|---|
| 3 · shared | `shared` | one net, every intersection | its own local obs | 31,241 |
| 1 · independent | `independent` | one net **per** intersection, neighbours blinded | its own local obs | N × 31,241 |
| 2 · CTDE | `central` | shared local actor | **global** state (all agents) | actor + central critic |

Build order is rung 3 → rung 1 → rung 2 (deployment target first, cheapest to
train, forces the plumbing). Narrative order is the reverse.

```bash
# rung 3 — the one that ships. ~a few minutes on CPU.
training/.venv/bin/python training/train.py --rung shared --level fourway-bays

# rung 1 — same trainer, sharing off, neighbours masked out of the obs.
training/.venv/bin/python training/train.py --rung independent --level grid2

# rung 2 — shared actor + centralized critic (drops onto rung 3's deploy path).
training/.venv/bin/python training/train.py --rung central --level grid2
```

Levels (all one obs schema, all Discrete(8)):

| level | intersections | character |
|---|---|---|
| `fourway`, `fourway-bays`, `fourway-bays2` | 1 | single intersection, bays add protected-left phases |
| `grid2`, `grid3`, `grid5` | 4 / 9 / 25 | uniform grid, even demand |
| `corridor` | 15 (5×3) | **road hierarchy**: fast two-way arterials (a busy E–W thoroughfare + an N–S one), a **one-way** eastbound side street, demand concentrated on the thoroughfares |
| `corridor-rush` | 15 | corridor with **time-varying** rush-hour demand (ramp / peak / drain) |
| `corridor7` | 21 (7×3) | wider corridor |

The corridor is built by `Signal.Core/CorridorBuilder.cs`. Dump any level's true
topology and render it:

```bash
dotnet Signal.EnvServer/bin/Release/net8.0/Signal.EnvServer.dll --dump corridor > /tmp/corridor.json
training/.venv/bin/python tools/render_network.py /tmp/corridor.json -o corridor-map.svg
```

## Evaluate & plot

```bash
training/.venv/bin/python training/evaluate.py training/runs/shared-fourway-bays-s0.pt
training/.venv/bin/python training/plot.py training/runs/*.json -o training/runs/curve.svg
```

`evaluate.py` scores the trained policy against three same-harness references
(`fixed` round-robin cycle, `random` legal, `hold`) on identical seeds, and
reports the pressure return **and** mean/worst approach wait together — a policy
that wins on average by starving one approach is caught by the worst-wait
column, not the average.

For the classical fixed-time / Greedy / MaxPressure numbers, cross-reference the
C# harness: `cd Signal.Headless && dotnet run -c Release -- bench --builtin
fourway-signal --seeds 5`. Those use link-level wait; the RL eval uses the obs
proxy, so read them as two views, not one scale.

## Design notes

- **Not SB3.** Masked multi-agent action spaces with shared parameters and a
  centralized critic are cleaner hand-rolled than bent around a single-agent
  API. Every (env, agent) pair is an independent actor along time; sharing
  lives in the policy object, not the loop — which is exactly why the shared
  policy gets N× the experience per gradient step.
- **Decision interval is a first-order knob** (`--decision-ticks`, default 50 =
  5 sim-sec). This is the hyperparameter the M1 MaxPressure bug was really
  about; sweep it before touching network width.
- **Auto-reset** is handled server-side; the trainer truncates GAE at the
  `done` boundary rather than bootstrapping across episodes.
- **Rung 1 blinds neighbours** by zeroing obs `[33:105]` (the four neighbour
  blocks), leaving each brain its own approaches + phase — rung 3 with
  coordination turned off, per the plan.
