# Change: Emergency vehicle preemption

## Why
Signals treat every vehicle alike. A fire engine waits at a red like any car. Preemption is the
standard answer: a signal that sees an emergency vehicle coming gives its approach a green.

## What Changes
- A vehicle can be marked as an emergency vehicle, and a simulation can spawn one on a given route.
- A signal controller that has an emergency vehicle within a set distance on one of its approaches
  requests the phase that serves that vehicle, in place of asking its policy.
- The safety envelope does not change: minimum green, yellow and all-red still apply.

## Out of scope
- Policies and observations do not learn about emergency vehicles. The observation vector keeps its
  size, so the shipped policy weights stay valid.
- The Godot view does not draw emergency vehicles differently.
- The environment server's protocol does not change.

## Impact
Filled in by Leyline: see `leyline.md`.
