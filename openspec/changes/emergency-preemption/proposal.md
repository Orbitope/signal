# Change: Emergency vehicle preemption

## Why
Signals treat every vehicle alike. A fire engine waits at a red like any car. Preemption is the
standard answer: a signal that sees an emergency vehicle coming gives its approach a green.

## What Changes
- A vehicle can be marked as an emergency vehicle, and a simulation can spawn one on a given route.
  It enters ahead of any cars held at its entrance.
- A signal controller that has an emergency vehicle within the preemption distance on one of its
  approaches requests the phase that serves that vehicle. It checks every tick.
- While that holds, the controller ignores every other phase request: its policy, a player's tap and
  a training agent's action.
- Signals with no emergency vehicle nearby are not affected.
- The safety envelope does not change: minimum green, yellow and all-red still apply.

## Out of scope
- Policies and observations do not learn about emergency vehicles. The observation vector keeps its
  size, so the shipped policy weights stay valid.
- The Godot view does not draw emergency vehicles differently.
- The environment server's protocol does not change. A training agent whose action is overruled is
  not told.
- The state hash is not extended to cover the emergency flag.

## Impact
Filled in by Leyline: see `leyline.md`.
