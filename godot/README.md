# Signal — Godot project

Godot 4.7.1 .NET (Godot.NET.Sdk 4.7.1), managed with fgvm: `.fgvm-version`
pins the version and `fgvm godot -- --path .` launches it. Open this folder in
the editor, or build from the CLI: `dotnet build` restores the SDK from
nuget.org and builds Signal.Core via ProjectReference.

**First run on a fresh checkout:** import resources once so the fonts load
(`.godot/` is not committed):

```
godot --headless --path . --import
```

- `scenes/main.tscn` — the level player (GAME_PLAN P0). Loads any built-in
  level from `Signal.Core.Levels`, or a LevelDef JSON via `--level=path.json`,
  fits the camera, and runs your sim against the lockstep MaxPressure ghost.
  MaxPressure drives every light; tap an approach to hold it green for 12 s.
  Keys: space pause · 1/2/4 speed · R restart · [ ] level · F refit ·
  wheel zoom · middle/right-drag or WASD pan. A results panel appears when
  the round ends.
  Dev hook: `godot --path . -- --level=corridor --screenshot=out.png --after=45`
  renders the level at speed, saves a PNG, and quits.
- `scenes/smoke.tscn` — CI gate, run headless:
  `godot --headless --path . res://scenes/smoke.tscn`
  (Core sims under Godot's .NET host, determinism, ghost lockstep, fixed-tick
  accounting, built-in levels run through the registry, tap override scoping
  and expiry, round finish; exits nonzero on failure)

CI without an editor: `dotnet build` works standalone; the smoke scene needs the
godot binary (mono build) on PATH.

## Design system

`scripts/Orbitope.cs` carries the ContentKit Palette B tokens (Void/Surface/Raised
backgrounds, Amber/Steel/Coral keys, Sage/Mauve/Terra data series) and loads
Rajdhani + JetBrains Mono from `fonts/` (OFL licenses included).

Semantic mapping — steel = flow, amber = friction, coral = crisis:
- vehicles: Steel calm -> Amber -> AmberBright as wait climbs
- lights: Sage green, Amber yellow (literal), Terra red
- spillback: the scene's single Coral element (accent rule), a fading pulse
- HUD: Surface panel, Rajdhani title, Mono stats; title dims when the AI leads
