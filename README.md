# Signal

A from-scratch traffic-signal simulator and a multi-agent reinforcement-learning
stack built on top of it. One small shared policy learns to run every
intersection, and beats a hand-tuned fixed-time plan on road networks from one
signal to twenty-five, the same 62-thousand-parameter model unchanged.

**Writeup, with live figures: https://orbitope.com/signal/**

## Layout

- `Signal.Core/` — the microsimulator (netstandard2.1, zero-dependency): IDM
  car-following, signalized intersections, turn restrictions, demand, and the
  network builders (grids, corridors, one-way couplets).
- `Signal.EnvServer/` — the RL environment over stdio (4-byte length-prefixed
  frames, JSON control plus raw float32 tensors).
- `training/` — from-scratch multi-agent PPO (not SB3): the three rungs
  (independent, shared, centralized critic), evaluation against fixed-time and
  random baselines, and the recorder behind the article's live figures.
  `RESULTS.md` has the measured numbers.
- `docs/` — the article at `orbitope.com/signal/`, a single self-contained HTML
  file with the figures inlined.
- `godot/` — early game prototype.
- `tools/` — network and figure renderers.

## Reproduce

```bash
bash training/run_all.sh
```

Built by Matthew Burke.
