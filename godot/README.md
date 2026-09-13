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

## What's in it

- **Menu** (`Menu.cs`) — home and the puzzle list with stars.
- **Puzzles** (`PuzzlePlay.cs`, GAME_PLAN P1) — World 1, eight one-junction
  puzzles from `Signal.Core.Worlds`. Left panel: the brief, goals, money, your
  changes, Run. Click the junction or an approach on the map for the tools
  this puzzle allows, each with a price. Run scores every seed instantly and
  then replays seed 0 on screen with the goals updating live. Stars save to
  `user://progress.json`. After two failed runs a "show me the answer" button
  appears.
- **Editor** (`Editor.cs`, GAME_PLAN P3) — grid-snapped. Junctions mode:
  click a cell to place or remove a junction. Streets mode: click two
  neighbouring junctions to join or unjoin them. Inspect mode: a junction's
  control, each arm's direction or "open to the outside", roundabout, timed
  plan; a street's bay or no-left. Traffic is a preset + total + rush toggle.
  Run it, Save/Load (`user://levels`), or Export puzzle with goals, a toolbox
  and a budget (`user://puzzles`), which then appears under Puzzles → Your
  puzzles. `--editor=1` opens it; `--popup=node|export` for screenshots.
- **Sandbox** (`Sandbox.cs`, GAME_PLAN P0) — any built-in level from
  `Signal.Core.Levels` or a LevelDef JSON via `--level=path.json`. The game AI
  drives every light; tap an approach to hold it green for 12 s; beat the
  lockstep AI ghost. Keys: space pause · 1/2/4 speed · R restart · [ ] level.
- Everywhere: wheel zoom · middle/right-drag or WASD pan · F fit ·
  **ctrl+= / ctrl+- scale the whole UI** (saved) · Esc to the menu.

The window opens at the project's 1280×800 times the display scale (Retina
gets 2560×1600) and the canvas stretches to it, so nothing renders half size.

## The AI

`policies/*.bin` are exported actors (`training/export_policy.py`). At startup
`Main.LoadGameAi` picks `shared-w1-v4-s0` for a lone junction and
`shared-grid3-flow-v4-s0` for anything with more than one signal; if a file is
missing the light-runner falls back to `AgingMaxPressurePolicy`. `--ai=mp`
forces the fallback. The sandbox HUD names the AI in play. Each `.parity.json`
next to a `.bin` is the fixture `Signal.Tests` uses to prove the C# forward
pass matches PyTorch.

## Dev hooks (no display needed)

```
godot --path . -- --level=corridor --screenshot=out.png --after=45
godot --path . -- --puzzle=w1-3 --answer=1 --run=1 --screenshot=out.png --after=60
godot --path . -- --puzzle=w1-1 --popup=node --screenshot=out.png --after=0
```

`--puzzle=ID` opens a puzzle; `--answer=1` loads the authored answer; `--run=1`
presses Run; `--popup=node|link:ID|retime` opens a tool popup. `--screenshot`
saves a PNG once the sim reaches `--after` seconds and quits. In zsh, split the
args (`${=args}`) if you build the command from a string.

Smoke gate (CI), headless:

```
godot --headless --path . res://scenes/smoke.tscn
```

39 checks: Core sims under Godot's .NET host, determinism, ghost lockstep,
fixed-tick accounting, registry levels run, tap override scoping and expiry,
round finish, edit ops build legal levels (roundabout macro, bays, refusal of
an edit that strands traffic), every World 1 puzzle fails as given and solves
with its authored answer, puzzle load honours a timed plan, the trained
policies load from res:// and drive a grid, an editor document builds, runs
and round-trips through user:// with an exported puzzle. Exits nonzero on
failure. `dotnet build` works standalone; the smoke scene needs the godot
binary (mono build) on PATH.

## Scripts

| file | role |
|---|---|
| `Main.cs` | app shell: shared world, mode switching, args, HiDPI window, UI scale |
| `Menu.cs` / `PuzzlePlay.cs` / `Sandbox.cs` / `Editor.cs` | the four modes (CanvasLayers) |
| `ToolPopup.cs` | the junction/approach tool popup shared by puzzles and the editor |
| `Store.cs` | user:// JSON for editor documents and exported puzzles |
| `SimRunner.cs` | the only place Godot time meets sim time; sandbox or puzzle load |
| `NetworkView.cs` | roads, per-approach control badges, compass, arcs, picking |
| `VehicleView.cs` | all cars in one MultiMesh, zoom-aware size, wait colour |
| `CameraRig.cs` | fit (with a left inset for the panel), zoom, pan |
| `TapOverridePolicy.cs` | game AI + 12 s tap hold (sandbox) |
| `Ui.cs` / `Orbitope.cs` / `Progress.cs` | widgets, palette and fonts, saved stars and settings |
| `LevelLoader.cs` | registry name or JSON path → LevelDef |
| `HeadlessSmoke.cs` | the gate above |

## Look

Palette and fonts from `Orbitope.cs` (Void/Surface/Raised, Amber, Steel,
Rajdhani + JetBrains Mono). In the game, legibility wins over parity with the
article: roads are lighter with edge lines and direction chevrons, signal
colours are saturated green/amber/red, stop signs are red octagons and yields
white triangles on the approach they apply to, cars ramp steel → amber as they
wait, spillback is the one coral element.
