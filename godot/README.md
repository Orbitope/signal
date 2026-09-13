# Signal — Godot project

Godot 4.6.3 .NET. Open this folder in the Godot editor; it builds Signal.Core
via ProjectReference automatically.

- `scenes/main.tscn` — tactical slice: tap approaches for green, race the
  lockstep MaxPressure ghost. Space = pause, 1/2/4 = speed.
- `scenes/smoke.tscn` — CI gate, run headless:
  `godot --headless res://scenes/smoke.tscn`
  (verifies Core sims under Godot's .NET host, determinism, ghost lockstep,
  fixed-tick accounting; exits nonzero on failure)

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
