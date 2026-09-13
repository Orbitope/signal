using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// The level editor (GAME_PLAN P3). Grid-snapped: click a cell to place a
    /// junction, click two neighbouring junctions to join them, inspect a
    /// junction to set its control and which arms open to the outside, and
    /// use the same tool popup as puzzles for roundabouts, bays, turn bans
    /// and timed plans. Demand comes from a preset and a total. Run it, save
    /// it, or export it as a puzzle with goals, a toolbox and a budget.
    /// </summary>
    public partial class Editor : CanvasLayer
    {
        public Main App;

        private const float PanelWidth = 390f;
        private const int WrapWidth = (int)PanelWidth - 40;
        private const ulong Seed = 777;

        private enum Mode { Junctions, Streets, Inspect }

        private EditorDoc _doc = EditorDoc.Starter();
        private LevelDef _applied;                  // last successful build, or null
        private Solution _sol => Solution.From(_doc.ops);
        private Mode _mode = Mode.Inspect;
        private bool _running, _built;
        private Vector2I? _pendingA;                // Streets mode: first junction picked

        private static readonly List<ToolDef> EditorTools = new()
        { Tools.Roundabout(), Tools.TimedPlan(), Tools.TurnBay(), Tools.NoLeft() };

        // Panel widgets.
        private LineEdit _name;
        private Button _bJunctions, _bStreets, _bInspect, _run;
        private Label _status, _problems, _stats;
        private OptionButton _preset, _loadPick;
        private HSlider _total, _duration;
        private CheckButton _rush;
        private Label _totalLabel, _durationLabel;
        private ToolPopup _popup;
        private PanelContainer _export;
        private VBoxContainer _exportCol;

        // ------------------------------------------------------------ lifecycle

        public void Enter()
        {
            if (!_built) Build();
            App.Net.ShowCompass = false;
            App.Net.Overlay = _doc;
            App.Net.ShowGrid = true;
            _running = false;
            _name.Text = _doc.name;
            SyncDemandWidgets();
            Rebuild();
            SetMode(Mode.Inspect);
            SetProcessUnhandledInput(true);
        }

        public void Exit()
        {
            App.Runner.TimeScale = 0f;
            App.Net.Overlay = null;
            App.Net.ShowGrid = false;
            _popup.Close();
            _export.Visible = false;
            SetProcessUnhandledInput(false);
        }

        // ------------------------------------------------------------ model

        /// <summary>Build the doc into a level and load it paused. Problems show in the panel.</summary>
        private void Rebuild()
        {
            _doc.name = _name.Text.Length > 0 ? _name.Text : "untitled";
            App.Net.Overlay = _doc;
            var problems = _doc.Problems();
            if (problems.Count == 0)
            {
                try
                {
                    _applied = _doc.Build();
                    App.Runner.Load(_applied, Seed, _doc.ops, withGhost: false, withTaps: false);
                    App.Runner.TimeScale = _running ? 2f : 0f;
                }
                catch (PuzzleException e) { problems.Add(e.Message); _applied = null; App.Runner.Clear(); }
                catch (System.InvalidOperationException e) { problems.Add(e.Message); _applied = null; App.Runner.Clear(); }
            }
            else { _applied = null; App.Runner.Clear(); }
            _problems.Text = problems.Count == 0 ? "" : "Fix before running: " + string.Join("; ", problems) + ".";
            _problems.Visible = problems.Count > 0;
            _run.Disabled = _applied == null;
            FitCamera();
            RefreshStatus();
        }

        private void FitCamera()
        {
            float s = _doc.spacing * App.Net.PixelsPerMeter;
            var rect = new Rect2(new Vector2(-0.5f, -0.5f) * s - new Vector2(1f, 1f) * (_doc.stub * App.Net.PixelsPerMeter),
                                 new Vector2(_doc.cols, _doc.rows) * s + new Vector2(2f, 2f) * (_doc.stub * App.Net.PixelsPerMeter));
            App.Cam.InsetLeft = PanelWidth;
            App.Cam.FitTo(rect);
        }

        private void AddOp(EditOp op)
        {
            var backup = _doc.Clone();
            var sol = Solution.From(_doc.ops);
            sol.Add(op);
            _doc.ops = sol.Ops;
            _popup.Close();
            Rebuild();
            if (_applied == null && _doc.Problems().Count == 0)
            {   // the op itself broke the level (e.g. stranded a flow): back out
                string why = _problems.Text;
                _doc = backup; Rebuild();
                _problems.Text = "Can't do that: " + why.Replace("Fix before running: ", ""); _problems.Visible = true;
            }
        }

        private void RemoveOp(EditOp op)
        {
            _doc.ops.RemoveAll(o => o.SameAs(op));
            _popup.Close();
            Rebuild();
        }

        private void SetMode(Mode m)
        {
            _mode = m; _pendingA = null;
            _popup.Close();
            _bJunctions.Text = (m == Mode.Junctions ? "• " : "") + "Junctions";
            _bStreets.Text = (m == Mode.Streets ? "• " : "") + "Streets";
            _bInspect.Text = (m == Mode.Inspect ? "• " : "") + "Inspect";
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            _status.Text = _running ? "Running. Stop to keep editing." : _mode switch
            {
                Mode.Junctions => "Click an empty cell to place a junction, a junction to remove it.",
                Mode.Streets => _pendingA == null ? "Click a junction, then a neighbouring junction, to join or unjoin them."
                                                  : $"Now click a junction next to ({_pendingA.Value.X},{_pendingA.Value.Y}).",
                _ => "Click a junction for its control and arms, or a street for lane tools.",
            };
        }

        // ------------------------------------------------------------ panel

        private void Build()
        {
            _built = true;
            var side = Ui.Panel();
            side.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.LeftWide);
            side.CustomMinimumSize = new Vector2(PanelWidth, 0);
            AddChild(side);
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            side.AddChild(scroll);
            var col = Ui.Column(8);
            col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(col);

            var head = Ui.Row(12);
            var titleLabel = Ui.Label("EDITOR", Ui.Title, Orbitope.TextBright, Orbitope.Rajdhani);
            titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            head.AddChild(titleLabel);
            head.AddChild(Ui.Button("Menu", () => App.ShowMenu()));
            col.AddChild(head);
            _name = new LineEdit { PlaceholderText = "level name", CustomMinimumSize = new Vector2(0, 40) };
            _name.AddThemeFontOverride("font", Orbitope.Mono);
            _name.AddThemeFontSizeOverride("font_size", Ui.Body);
            col.AddChild(_name);

            var modes = Ui.Row(6);
            _bJunctions = Ui.Button("Junctions", () => SetMode(Mode.Junctions), size: Ui.Small + 1);
            _bStreets = Ui.Button("Streets", () => SetMode(Mode.Streets), size: Ui.Small + 1);
            _bInspect = Ui.Button("Inspect", () => SetMode(Mode.Inspect), size: Ui.Small + 1);
            foreach (var b in new[] { _bJunctions, _bStreets, _bInspect }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; modes.AddChild(b); }
            col.AddChild(modes);
            _status = Ui.Label("", Ui.Body, Orbitope.TextSecondary, null, wrap: true, width: WrapWidth);
            col.AddChild(_status);
            _problems = Ui.Label("", Ui.Body, Ui.Bad, null, wrap: true, width: WrapWidth);
            _problems.Visible = false;
            col.AddChild(_problems);
            col.AddChild(Ui.Separator());

            col.AddChild(Ui.Label("TRAFFIC", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
            _preset = new OptionButton();
            _preset.AddThemeFontOverride("font", Orbitope.Mono);
            _preset.AddThemeFontSizeOverride("font_size", Ui.Body);
            foreach (var (label, _) in Presets) _preset.AddItem(label);
            _preset.ItemSelected += i => { _doc.demand.preset = Presets[(int)i].key; Rebuild(); };
            col.AddChild(_preset);
            _totalLabel = Ui.Body_("");
            col.AddChild(_totalLabel);
            _total = new HSlider { MinValue = 4, MaxValue = 80, Step = 2, CustomMinimumSize = new Vector2(0, 28) };
            _total.ValueChanged += v => { _doc.demand.total = (float)v; _totalLabel.Text = $"Total traffic: {v:F0} cars/min"; };
            _total.DragEnded += _ => Rebuild();
            col.AddChild(_total);
            _rush = new CheckButton { Text = "Rush hour in the middle" };
            _rush.AddThemeFontOverride("font", Orbitope.Mono);
            _rush.AddThemeFontSizeOverride("font_size", Ui.Body);
            _rush.Toggled += on => { _doc.demand.rush = on; Rebuild(); };
            col.AddChild(_rush);
            _durationLabel = Ui.Body_("");
            col.AddChild(_durationLabel);
            _duration = new HSlider { MinValue = 120, MaxValue = 900, Step = 30, CustomMinimumSize = new Vector2(0, 28) };
            _duration.ValueChanged += v => { _doc.duration = (float)v; _durationLabel.Text = $"Length: {v:F0} s"; };
            _duration.DragEnded += _ => Rebuild();
            col.AddChild(_duration);
            col.AddChild(Ui.Separator());

            _stats = Ui.Label("", Ui.Body, Orbitope.TextPrimary, null, wrap: true, width: WrapWidth);
            col.AddChild(_stats);
            var runRow = Ui.Row(8);
            _run = Ui.Button("Run", ToggleRun, primary: true, size: 20);
            _run.CustomMinimumSize = new Vector2(150, 48);
            runRow.AddChild(_run);
            runRow.AddChild(Ui.Button("Faster", () => { if (_running) App.Runner.TimeScale = App.Runner.TimeScale >= 8f ? 2f : 8f; }));
            col.AddChild(runRow);
            col.AddChild(Ui.Separator());

            col.AddChild(Ui.Label("FILE", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
            var fileRow = Ui.Row(8);
            fileRow.AddChild(Ui.Button("Save", Save));
            fileRow.AddChild(Ui.Button("New", () => { _doc = EditorDoc.Starter(); _name.Text = _doc.name; SyncDemandWidgets(); Rebuild(); }));
            fileRow.AddChild(Ui.Button("Export puzzle", OpenExport));
            col.AddChild(fileRow);
            var loadRow = Ui.Row(8);
            _loadPick = new OptionButton();
            _loadPick.AddThemeFontOverride("font", Orbitope.Mono);
            _loadPick.AddThemeFontSizeOverride("font_size", Ui.Body);
            _loadPick.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            loadRow.AddChild(_loadPick);
            loadRow.AddChild(Ui.Button("Load", Load));
            col.AddChild(loadRow);
            RefreshLoadList();
            col.AddChild(Ui.Label("wheel zoom · drag pan · F fit · Esc menu", Ui.Small, Orbitope.TextMuted, null, wrap: true, width: WrapWidth));

            var host = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(host);
            _popup = new ToolPopup { Net = App.Net, OnAdd = AddOp, OnRemove = RemoveOp, ShowPrices = false };
            host.AddChild(_popup);

            // Export dialog, centred.
            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            center.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(center);
            _export = Ui.Panel();
            _export.Visible = false;
            _export.CustomMinimumSize = new Vector2(560, 0);
            center.AddChild(_export);
            _exportCol = Ui.Column(8);
            _export.AddChild(_exportCol);
        }

        private static readonly (string label, string key)[] Presets =
        {
            ("Balanced: every entry to every exit", "balanced"),
            ("East-west heavy", "east-west"),
            ("North-south heavy", "north-south"),
        };

        private void SyncDemandWidgets()
        {
            int pi = System.Array.FindIndex(Presets, p => p.key == _doc.demand.preset);
            _preset.Selected = pi < 0 ? 0 : pi;
            _total.Value = _doc.demand.total; _totalLabel.Text = $"Total traffic: {_doc.demand.total:F0} cars/min";
            _rush.ButtonPressed = _doc.demand.rush;
            _duration.Value = _doc.duration; _durationLabel.Text = $"Length: {_doc.duration:F0} s";
        }

        private void ToggleRun()
        {
            if (_applied == null) return;
            _running = !_running;
            _run.Text = _running ? "Stop" : "Run";
            _popup.Close();
            App.Runner.Reset();
            App.Runner.TimeScale = _running ? 2f : 0f;
            RefreshStatus();
        }

        private void Save()
        {
            _doc.name = _name.Text.Length > 0 ? _name.Text : "untitled";
            string path = Store.SaveDoc(_doc);
            _stats.Text = $"Saved {path}";
            RefreshLoadList();
        }

        private void RefreshLoadList()
        {
            _loadPick.Clear();
            foreach (var n in Store.DocNames()) _loadPick.AddItem(n);
            _loadPick.Disabled = _loadPick.ItemCount == 0;
        }

        private void Load()
        {
            if (_loadPick.ItemCount == 0) return;
            var doc = Store.LoadDoc(_loadPick.GetItemText(_loadPick.Selected));
            if (doc == null) return;
            _doc = doc; _name.Text = doc.name;
            SyncDemandWidgets();
            Rebuild();
        }

        // ------------------------------------------------------------ export

        private void OpenExport()
        {
            if (_applied == null) return;
            foreach (var c in _exportCol.GetChildren()) c.QueueFree();
            _exportCol.AddChild(Ui.Label("EXPORT AS PUZZLE", Ui.Heading, Orbitope.TextBright, Orbitope.Rajdhani));
            _exportCol.AddChild(Ui.Label("Your junction settings and changes become the starting state. Players get the tools you tick and the budget you set.", Ui.Small, Orbitope.TextSecondary, null, wrap: true, width: 520));

            var title = new LineEdit { Text = _doc.name, PlaceholderText = "title", CustomMinimumSize = new Vector2(0, 38) };
            var intro = new LineEdit { PlaceholderText = "one-line brief for the player", CustomMinimumSize = new Vector2(0, 38) };
            foreach (var le in new[] { title, intro }) { le.AddThemeFontOverride("font", Orbitope.Mono); le.AddThemeFontSizeOverride("font_size", Ui.Body); _exportCol.AddChild(le); }

            _exportCol.AddChild(Ui.Label("Goals", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
            var goals = new List<(CheckButton on, SpinBox v, ObjectiveKind kind)>();
            foreach (var (kind, label, def) in new[] { (ObjectiveKind.AvgWait, "Average wait under (s)", 20.0), (ObjectiveKind.MaxWait, "Nobody waits more than (s)", 90.0), (ObjectiveKind.ClearCars, "Get at least this many cars through", 100.0), (ObjectiveKind.NoSpillback, "No spillback", 0.0) })
            {
                var row = Ui.Row(8);
                var on = new CheckButton { Text = label, ButtonPressed = kind == ObjectiveKind.AvgWait };
                on.AddThemeFontOverride("font", Orbitope.Mono); on.AddThemeFontSizeOverride("font_size", Ui.Body);
                on.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(on);
                SpinBox v = null;
                if (kind != ObjectiveKind.NoSpillback)
                {
                    v = new SpinBox { MinValue = 1, MaxValue = 2000, Step = 1, Value = def, CustomMinimumSize = new Vector2(110, 36) };
                    row.AddChild(v);
                }
                goals.Add((on, v, kind));
                _exportCol.AddChild(row);
            }

            _exportCol.AddChild(Ui.Label("Toolbox", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
            var allTools = new[] { Tools.Signal(), Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Roundabout(), Tools.TurnBay(), Tools.NoLeft(), Tools.OneWay(), Tools.TimedPlan() };
            var ticks = new List<(CheckButton on, ToolDef tool)>();
            var grid = new GridContainer { Columns = 2 };
            foreach (var t in allTools)
            {
                var cb = new CheckButton { Text = $"{t.label}  ${t.price}", ButtonPressed = true };
                cb.AddThemeFontOverride("font", Orbitope.Mono); cb.AddThemeFontSizeOverride("font_size", Ui.Body);
                grid.AddChild(cb); ticks.Add((cb, t));
            }
            _exportCol.AddChild(grid);

            var money = Ui.Row(12);
            money.AddChild(Ui.Body_("Budget $"));
            var budget = new SpinBox { MinValue = 0, MaxValue = 500, Step = 5, Value = 60, CustomMinimumSize = new Vector2(110, 36) };
            money.AddChild(budget);
            money.AddChild(Ui.Body_("Par $"));
            var par = new SpinBox { MinValue = 0, MaxValue = 500, Step = 5, Value = 40, CustomMinimumSize = new Vector2(110, 36) };
            money.AddChild(par);
            _exportCol.AddChild(money);

            var result = Ui.Label("", Ui.Body, Orbitope.TextSecondary, null, wrap: true, width: 520);
            _exportCol.AddChild(result);
            var buttons = Ui.Row(8);
            buttons.AddChild(Ui.Button("Export", () =>
            {
                var p = new PuzzleDef
                {
                    id = "user-" + Store.Slug(title.Text.Length > 0 ? title.Text : _doc.name),
                    title = title.Text.Length > 0 ? title.Text : _doc.name,
                    intro = intro.Text,
                    level = _doc.BuildRaw(),
                    budget = (int)budget.Value, par = (int)par.Value,
                };
                foreach (var op in _doc.ops) p.initialOps.Add(op.Clone());
                foreach (var (on, v, kind) in goals)
                    if (on.ButtonPressed) p.objectives.Add(new ObjectiveDef { kind = kind, value = v == null ? 0f : (float)v.Value });
                foreach (var (on, tool) in ticks) if (on.ButtonPressed) p.toolbox.Add(tool);
                if (p.objectives.Count == 0) { result.Text = "Tick at least one goal."; return; }
                var check = PuzzleScorer.Evaluate(p, p.initialOps);
                string path = Store.SavePuzzle(p);
                result.Text = check.Error != null ? "Saved, but the start state fails to run: " + check.Error
                            : check.Solved ? "Saved. Note: the starting state already meets every goal, so it's solved before the player touches it."
                            : $"Saved to {path}. Find it under Puzzles → Your puzzles.";
            }, primary: true));
            buttons.AddChild(Ui.Button("Close", () => _export.Visible = false));
            _exportCol.AddChild(buttons);
            _export.Visible = true;
        }

        // ------------------------------------------------------------ junction popup

        private void OpenJunction(JunctionDef j)
        {
            int nodeId = EditorDoc.JunctionId(j.gx, j.gy);
            var ring = _doc.ops.Find(o => o.kind == EditKind.Roundabout && o.node == nodeId);
            var baseLevel = _applied ?? SafeRaw();
            string current = ring != null ? null : Edits.ControlName(j.control, j.majorAxis) + (_doc.ops.Exists(o => o.kind == EditKind.Retime && o.node == nodeId) ? " on a timed plan" : "");
            System.Action<VBoxContainer> extra = colx =>
            {
                colx.AddChild(Ui.Label("Control", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
                foreach (var (label, ct, axis) in new[] { ("Traffic signal", ControlType.Signalized, 0), ("All-way stop", ControlType.AllWayStop, 0), ("Two-way stop: E-W keeps priority", ControlType.TwoWayStop, 0), ("Two-way stop: N-S keeps priority", ControlType.TwoWayStop, 1) })
                {
                    bool now = j.control == ct && (ct != ControlType.TwoWayStop || j.majorAxis == axis);
                    colx.AddChild(Ui.Button((now ? "• " : "") + label, () =>
                    {
                        j.control = ct; j.majorAxis = axis;
                        _doc.ops.RemoveAll(o => o.kind == EditKind.Roundabout && o.node == nodeId);
                        _popup.Close(); Rebuild();
                    }, size: Ui.Small + 1));
                }
                colx.AddChild(Ui.Label("Arms", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
                for (int d = 0; d < 4; d++)
                {
                    int dir = d;
                    var nb = _doc.NeighbourVia(j, dir, out var street);
                    if (nb != null)
                    {
                        bool fwd = (street.ax == j.gx && street.ay == j.gy) ? street.ab : street.ba;
                        bool back = (street.ax == j.gx && street.ay == j.gy) ? street.ba : street.ab;
                        string state = fwd && back ? "two-way" : fwd ? "one-way, outbound" : back ? "one-way, inbound" : "closed";
                        colx.AddChild(Ui.Button($"{EditorDoc.DirName[dir]}: street, {state}  (change)", () =>
                        {
                            // Cycle: two-way -> outbound only -> inbound only -> two-way.
                            bool mine = street.ax == j.gx && street.ay == j.gy;
                            ref bool f = ref (mine ? ref street.ab : ref street.ba);
                            ref bool b = ref (mine ? ref street.ba : ref street.ab);
                            if (f && b) { b = false; } else if (f) { f = false; b = true; } else { f = true; b = true; }
                            _popup.Close(); Rebuild();
                        }, size: Ui.Small + 1));
                    }
                    else
                    {
                        bool open = j.Open(dir);
                        colx.AddChild(Ui.Button($"{EditorDoc.DirName[dir]}: {(open ? "open to the outside" : "closed")}  (toggle)", () =>
                        {
                            j.SetOpen(dir, !open);
                            _popup.Close(); Rebuild();
                        }, size: Ui.Small + 1));
                    }
                }
                colx.AddChild(Ui.Button("Remove this junction", () => { _doc.RemoveJunction(j.gx, j.gy); _popup.Close(); Rebuild(); }, size: Ui.Small + 1));
                colx.AddChild(Ui.Separator());
                colx.AddChild(Ui.Label("More", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
            };
            var sol = Solution.From(_doc.ops);
            if (ring != null) _popup.OpenRoundabout(ring, baseLevel, _applied, EditorTools, sol, null, extra);
            else _popup.OpenNode(nodeId, current, baseLevel, _applied, EditorTools, sol, null, extra);
        }

        private LevelDef SafeRaw()
        {
            try { return _doc.BuildRaw(); } catch { return new LevelDef(); }
        }

        /// <summary>Dev hook (--popup=node | export): open a panel for a screenshot.</summary>
        public void DebugOpen(string spec)
        {
            if (spec == "node" && _doc.junctions.Count > 0) OpenJunction(_doc.junctions[0]);
            else if (spec == "export") OpenExport();
        }

        // ------------------------------------------------------------ input

        private Vector2I CellAt(Vector2 world)
        {
            float s = _doc.spacing * App.Net.PixelsPerMeter;
            return new Vector2I(Mathf.RoundToInt(world.X / s), Mathf.RoundToInt(world.Y / s));
        }

        private bool InGrid(Vector2I c) => c.X >= 0 && c.Y >= 0 && c.X < _doc.cols && c.Y < _doc.rows;

        private bool NearCell(Vector2 world, Vector2I c)
        {
            float s = _doc.spacing * App.Net.PixelsPerMeter;
            return world.DistanceTo(new Vector2(c.X, c.Y) * s) < s * 0.35f;
        }

        public override void _Process(double delta)
        {
            if (!Visible) return;
            if (_running && App.Runner.Sim != null)
            {
                var r = App.Runner;
                string speed = r.TimeScale <= 0f ? "paused" : $"{r.TimeScale:F0}x";
                _stats.Text = $"t {r.Sim.Time:F0} s of {_doc.duration:F0} s ({speed})   AI: {r.AiName}\n" +
                              $"avg wait {r.Sim.Metrics.LiveAvgWait(r.Sim):F1} s   worst {r.Sim.Metrics.MaxWait:F0} s\n" +
                              $"done {r.Sim.Metrics.Completed}   in system {r.Sim.VehiclesInSystem()}   spillbacks {r.Sim.Metrics.SpillbackEvents}";
                if (r.Finished) { _running = false; _run.Text = "Run"; RefreshStatus(); }
            }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (!Visible) return;
            if (ev is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
            if (_running || _export.Visible) return;
            var world = App.Net.GetGlobalMousePosition();
            var cell = CellAt(world);
            bool onCell = InGrid(cell) && NearCell(world, cell);
            var j = onCell ? _doc.JunctionAt(cell.X, cell.Y) : null;

            switch (_mode)
            {
                case Mode.Junctions:
                    if (!onCell) break;
                    if (j != null) _doc.RemoveJunction(cell.X, cell.Y);
                    else _doc.AddJunction(cell.X, cell.Y);
                    Rebuild();
                    break;
                case Mode.Streets:
                    if (j == null) { _pendingA = null; RefreshStatus(); break; }
                    if (_pendingA == null) { _pendingA = cell; RefreshStatus(); break; }
                    var a = _pendingA.Value; _pendingA = null;
                    if (EditorDoc.Adjacent(a.X, a.Y, cell.X, cell.Y))
                    {
                        if (_doc.StreetBetween(a.X, a.Y, cell.X, cell.Y) != null) _doc.Disconnect(a.X, a.Y, cell.X, cell.Y);
                        else _doc.Connect(a.X, a.Y, cell.X, cell.Y);
                        Rebuild();
                    }
                    else RefreshStatus();
                    break;
                default:
                    if (j != null) { OpenJunction(j); break; }
                    if (App.Runner.Sim != null && App.Net.TryPickLink(world, out int linkId))
                        _popup.OpenLink(linkId, _applied, EditorTools, Solution.From(_doc.ops), null);
                    else _popup.Close();
                    break;
            }
        }
    }
}
