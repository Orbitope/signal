using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// Build-and-run puzzle screen (GAME_PLAN P1). Left: the brief, objectives,
    /// money, your changes, Run. Map: click a junction or an approach to get
    /// the tools that apply there (ToolPopup). Run scores every seed instantly
    /// and then plays seed 0 on screen, so what you watch is what was scored.
    /// Works for any puzzle set: a world's list or the player's own exports.
    /// </summary>
    public partial class PuzzlePlay : CanvasLayer
    {
        public Main App;

        private const float PanelWidth = 390f;
        private const int WrapWidth = (int)PanelWidth - 40;

        private enum State { Build, Running, Done }

        private PuzzleDef _p;
        private IList<PuzzleDef> _set;
        private string _setTitle = "";
        private Solution _sol = new();
        private LevelDef _applied;
        private PuzzleResult _result;
        private State _state;
        private int _fails;
        private bool _showHint, _built;

        private Label _title, _sub, _intro, _hintLabel, _budget, _error, _status, _verdict, _tutorial;
        private PanelContainer _tutorialBox;
        private VBoxContainer _objRows, _opsRows;
        private HBoxContainer _buildButtons, _runButtons, _doneButtons;
        private Button _run, _reveal;
        private readonly List<Label> _objLabels = new();
        private ToolPopup _popup;

        // ------------------------------------------------------------ lifecycle

        public void Enter(PuzzleDef p, IList<PuzzleDef> set, string setTitle)
        {
            if (!_built) Build();
            _p = p; _set = set; _setTitle = setTitle;
            _sol = Solution.From(p.initialOps);
            _fails = 0; _showHint = false; _result = null;
            _state = State.Build;
            App.Runner.FinishedRound += OnFinishedRound;
            App.Net.ShowCompass = true;
            _title.Text = p.title.ToUpperInvariant();
            _sub.Text = $"{setTitle} · puzzle {IndexOf(p) + 1} of {set.Count}";
            _intro.Text = p.intro;
            Rebuild();
            _popup.Close();
            RefreshPanel();
            SetProcessUnhandledInput(true);
        }

        public void Exit()
        {
            App.Runner.FinishedRound -= OnFinishedRound;
            App.Runner.TimeScale = 0f;
            _popup.Close();
            SetProcessUnhandledInput(false);
        }

        private int IndexOf(PuzzleDef p) => _set?.IndexOf(p) ?? 0;

        // ------------------------------------------------------------ model

        private string Rebuild()
        {
            try { _applied = Edits.Apply(_p.level, _sol.Ops); }
            catch (PuzzleException e) { return e.Message; }
            catch (System.InvalidOperationException e) { return e.Message; }
            App.Runner.Load(_applied, PuzzleScorer.SeedFor(_p, 0), _sol.Ops, withGhost: false, withTaps: false);
            App.Runner.TimeScale = 0f;
            App.FitCamera(PanelWidth);
            return null;
        }

        private void TryAddOp(EditOp op)
        {
            var backup = Solution.From(_sol.Ops);
            _sol.Add(op);
            string err = Rebuild();
            if (err != null) { _sol = backup; Rebuild(); ShowError("Can't do that: " + err + "."); }
            else ShowError(null);
            _popup.Close();
            RefreshPanel();
        }

        private void RemoveOp(EditOp op)
        {
            _sol.Remove(op);
            string err = Rebuild();
            if (err != null) ShowError("Can't do that: " + err + ".");
            _popup.Close();
            RefreshPanel();
        }

        /// <summary>Dev hook (--popup=node | link:ID | retime): open a tool popup for a screenshot.</summary>
        public void DebugOpenPopup(string spec)
        {
            if (spec == "node")
            {
                foreach (var n in App.Runner.Sim.Network.Nodes) if (NetworkView.IsEditable(n)) { OpenNode(n.Id); return; }
                foreach (var op in _sol.Ops) if (op.kind == EditKind.Roundabout) { _popup.OpenRoundabout(op, _p.level, _applied, _p.toolbox, _sol, _p.initialOps); return; }
            }
            else if (spec.StartsWith("link:") && int.TryParse(spec.Substring(5), out int id))
                _popup.OpenLink(id, _p.level, _p.toolbox, _sol, _p.initialOps);
            else if (spec == "retime")
            {
                var tool = _p.toolbox.Find(t => t.kind == EditKind.Retime);
                if (tool != null) foreach (var n in App.Runner.Sim.Network.Nodes) if (NetworkView.IsEditable(n)) { OpenNode(n.Id); break; }
            }
        }

        public void LoadAnswer()
        {
            if (_p?.answer == null || _p.answer.Count == 0) return;
            _sol = Solution.From(_p.answer);
            Rebuild();
            _popup.Close();
            RefreshPanel();
        }

        private int Cost()
        {
            try { return _sol.Cost(_p); } catch (PuzzleException) { return 0; }
        }

        public void Run()
        {
            if (_state != State.Build || _p == null) return;
            _result = PuzzleScorer.Evaluate(_p, _sol.Ops);
            if (_result.Error != null) { ShowError("Can't run: " + _result.Error + "."); return; }
            App.Runner.Reset();
            App.Runner.TimeScale = 2f;
            _state = State.Running;
            _popup.Close();
            RefreshPanel();
        }

        private void OnFinishedRound()
        {
            if (_state != State.Running || _result == null) return;
            _state = State.Done;
            if (_result.Solved) Progress.Record(_p.id, _result.Stars);
            else _fails++;
            RefreshPanel();
        }

        private void BackToBuild()
        {
            _state = State.Build;
            App.Runner.Reset();
            App.Runner.TimeScale = 0f;
            RefreshPanel();
        }

        private void NextPuzzle()
        {
            int i = IndexOf(_p);
            if (_set != null && i + 1 < _set.Count) App.ShowPuzzle(_set[i + 1], _set, _setTitle);
            else App.ShowPuzzleList();
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
            var col = Ui.Column(10);
            col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(col);

            _title = Ui.Label("", Ui.Title, Orbitope.TextBright, Orbitope.Rajdhani, wrap: true, width: WrapWidth);
            _sub = Ui.Label("", Ui.Small, Orbitope.TextMuted);
            _intro = Ui.Label("", Ui.Body, Orbitope.TextPrimary, null, wrap: true, width: WrapWidth);
            _hintLabel = Ui.Label("", Ui.Body, Orbitope.AmberBright, null, wrap: true, width: WrapWidth);
            _hintLabel.Visible = false;
            col.AddChild(_title); col.AddChild(_sub); col.AddChild(_intro); col.AddChild(_hintLabel);
            _tutorialBox = new PanelContainer();
            _tutorialBox.AddThemeStyleboxOverride("panel", Ui.Flat(Orbitope.Amber with { A = 0.16f }, Orbitope.Amber, 12, 10));
            _tutorial = Ui.Label("", Ui.Body, Orbitope.AmberBright, null, wrap: true, width: WrapWidth - 24);
            _tutorialBox.AddChild(_tutorial);
            _tutorialBox.Visible = false;
            col.AddChild(_tutorialBox);
            col.AddChild(Ui.Separator());

            col.AddChild(Ui.Label("GOALS", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
            _objRows = Ui.Column(4);
            col.AddChild(_objRows);
            col.AddChild(Ui.Separator());

            _budget = Ui.Label("", Ui.Body, Orbitope.TextPrimary, null, wrap: true, width: WrapWidth);
            col.AddChild(_budget);
            col.AddChild(Ui.Label("YOUR CHANGES", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
            _opsRows = Ui.Column(4);
            col.AddChild(_opsRows);
            _error = Ui.Label("", Ui.Body, Ui.Bad, null, wrap: true, width: WrapWidth);
            _error.Visible = false;
            col.AddChild(_error);
            col.AddChild(Ui.Separator());

            _status = Ui.Label("", Ui.Body, Orbitope.TextSecondary, null, wrap: true, width: WrapWidth);
            _verdict = Ui.Label("", Ui.Heading, Orbitope.TextBright, Orbitope.Rajdhani, wrap: true, width: WrapWidth);
            col.AddChild(_verdict); col.AddChild(_status);

            _buildButtons = Ui.Row(8);
            _run = Ui.Button("Run", Run, primary: true, size: 20);
            _run.CustomMinimumSize = new Vector2(150, 48);
            _buildButtons.AddChild(_run);
            _buildButtons.AddChild(Ui.Button("Hint", () => { _showHint = !_showHint; RefreshPanel(); }));
            _buildButtons.AddChild(Ui.Button("Reset", () => { _sol = Solution.From(_p.initialOps); Rebuild(); _popup.Close(); RefreshPanel(); }));
            col.AddChild(_buildButtons);
            _reveal = Ui.Button("Show me the answer", LoadAnswer);
            _reveal.Visible = false;
            col.AddChild(_reveal);

            _runButtons = Ui.Row(8);
            _runButtons.AddChild(Ui.Button("Faster", () => App.Runner.TimeScale = App.Runner.TimeScale >= 8f ? 2f : 8f));
            _runButtons.AddChild(Ui.Button("Stop and edit", BackToBuild));
            col.AddChild(_runButtons);

            _doneButtons = Ui.Row(8);
            _doneButtons.AddChild(Ui.Button("Next puzzle", NextPuzzle, primary: true));
            _doneButtons.AddChild(Ui.Button("Try again", BackToBuild));
            col.AddChild(_doneButtons);

            col.AddChild(Ui.Spacer(4));
            var nav = Ui.Row(8);
            nav.AddChild(Ui.Button("All puzzles", () => App.ShowPuzzleList()));
            nav.AddChild(Ui.Button("Menu", () => App.ShowMenu()));
            col.AddChild(nav);
            col.AddChild(Ui.Label("Click a junction or a street to change it · wheel zoom · drag pan · F fit", Ui.Small, Orbitope.TextMuted, null, wrap: true, width: WrapWidth));

            var host = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(host);
            _popup = new ToolPopup { Net = App.Net, OnAdd = TryAddOp, OnRemove = RemoveOp };
            host.AddChild(_popup);
        }

        private void ShowError(string msg)
        {
            _error.Text = msg ?? "";
            _error.Visible = msg != null;
        }

        private void RefreshPanel()
        {
            if (_p == null) return;
            _hintLabel.Visible = _showHint;
            _hintLabel.Text = "Hint: " + _p.hint;
            // Tutorial callout: until the player makes a change (or has already cleared this puzzle).
            bool untouched = _sol.Ops.Count == _p.initialOps.Count && _sol.Ops.TrueForAll(o => _p.initialOps.Exists(i => i.SameAs(o)));
            _tutorialBox.Visible = _state == State.Build && !string.IsNullOrEmpty(_p.tutorial) && untouched && Progress.StarsFor(_p.id) == 0;
            _tutorial.Text = _p.tutorial;

            foreach (var c in _objRows.GetChildren()) c.QueueFree();
            _objLabels.Clear();
            for (int i = 0; i < _p.objectives.Count; i++)
            {
                var l = Ui.Label("", Ui.Body, Orbitope.TextPrimary, null, wrap: true, width: WrapWidth);
                _objRows.AddChild(l);
                _objLabels.Add(l);
            }
            UpdateObjectiveRows();

            int cost = Cost();
            int left = _p.budget - cost;
            _budget.Text = $"Budget ${_p.budget}  spent ${cost}  left ${left}\n(three stars at ${_p.par} or less)";
            _budget.AddThemeColorOverride("font_color", left < 0 ? Ui.Bad : Orbitope.TextPrimary);

            foreach (var c in _opsRows.GetChildren()) c.QueueFree();
            if (_sol.Ops.Count == 0)
                _opsRows.AddChild(Ui.Label("none yet", Ui.Body, Orbitope.TextMuted));
            foreach (var op in _sol.Ops)
            {
                var row = Ui.Row(8);
                bool given = _p.initialOps.Exists(i => i.SameAs(op));
                var tool = _p.ToolFor(op);
                string price = given ? "given" : tool != null ? $"${tool.price}" : "?";
                var l = Ui.Label($"{Edits.Describe(op, _p.level)}  ({price})", Ui.Body, given ? Orbitope.TextSecondary : Orbitope.TextPrimary, null, wrap: true, width: WrapWidth - 60);
                l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(l);
                if (_state == State.Build)
                {
                    var opCopy = op;
                    var x = Ui.Button("x", () => RemoveOp(opCopy), size: Ui.Small);
                    x.CustomMinimumSize = new Vector2(36, 32);
                    row.AddChild(x);
                }
                _opsRows.AddChild(row);
            }

            _buildButtons.Visible = _state == State.Build;
            _runButtons.Visible = _state == State.Running;
            _doneButtons.Visible = _state == State.Done;
            _reveal.Visible = _state == State.Build && _fails >= 2 && _p.answer != null && _p.answer.Count > 0;
            _run.Disabled = left < 0;
            _verdict.Visible = _state == State.Done;
            if (_state == State.Done && _result != null)
            {
                if (_result.Solved)
                {
                    _verdict.Text = $"SOLVED  {Ui.Stars(_result.Stars)}";
                    _verdict.AddThemeColorOverride("font_color", Orbitope.AmberBright);
                    _status.Text = _result.Stars == 3 ? $"Spent ${_result.Cost}. That's the best builders' price."
                                 : $"Spent ${_result.Cost}. Three stars for ${_p.par} or less.";
                }
                else
                {
                    _verdict.Text = "NOT SOLVED";
                    _verdict.AddThemeColorOverride("font_color", Ui.Bad);
                    var failed = _result.Objectives.Find(o => !o.Pass);
                    _status.Text = failed == null ? "" :
                        $"{failed.Def.Describe()}: got {failed.Def.Format(failed.Worst)} on the worst of {_p.seeds.Count} runs.";
                }
            }
            else if (_state == State.Build)
                _status.Text = left < 0 ? "Over budget. Remove something before you run." : "Change the junction, then press Run. Every goal must hold on all three runs.";
        }

        private void UpdateObjectiveRows()
        {
            for (int i = 0; i < _objLabels.Count && i < _p.objectives.Count; i++)
            {
                var o = _p.objectives[i];
                var l = _objLabels[i];
                string text = o.Describe();
                Color c = Orbitope.TextPrimary;
                if (_state == State.Running && App.Runner.Sim != null)
                {
                    float v = o.Measure(App.Runner.Sim);
                    text += $"\n    so far: {o.Format(v)}";
                    c = o.Passes(v) ? Ui.Good : Orbitope.AmberBright;
                }
                else if (_state == State.Done && _result != null && i < _result.Objectives.Count)
                {
                    var r = _result.Objectives[i];
                    text = (r.Pass ? "OK  " : "NO  ") + text + $"\n    worst run: {o.Format(r.Worst)}";
                    c = r.Pass ? Ui.Good : Ui.Bad;
                }
                else text = "-   " + text;
                l.Text = text;
                l.AddThemeColorOverride("font_color", c);
            }
        }

        private string CurrentControlName(int nodeId)
        {
            var retime = _sol.Find(o => o.kind == EditKind.Retime && o.node == nodeId);
            var nd = _applied.network.nodes.Find(n => n.id == nodeId);
            if (nd == null) return "Roundabout";
            if (nd.control == ControlType.Signalized) return retime != null ? "Traffic signal on a timed plan" : "Traffic signal run by the AI";
            if (nd.control == ControlType.TwoWayStop)
            {
                var op = _sol.Find(o => o.kind == EditKind.SetControl && o.node == nodeId);
                return Edits.ControlName(ControlType.TwoWayStop, op?.majorAxis ?? 0);
            }
            return Edits.ControlName(nd.control);
        }

        private void OpenNode(int nodeId)
            => _popup.OpenNode(nodeId, CurrentControlName(nodeId), _p.level, _applied, _p.toolbox, _sol, _p.initialOps);

        // ------------------------------------------------------------ loop / input

        public override void _Process(double delta)
        {
            if (!Visible || _p == null) return;
            if (_state == State.Running)
            {
                UpdateObjectiveRows();
                var r = App.Runner;
                string speed = r.TimeScale <= 0f ? "paused" : $"{r.TimeScale:F0}x";
                _status.Text = $"Running: {r.Sim.Time:F0} s of {_p.level.duration:F0} s   ({speed})\n" +
                               $"cars done {r.Sim.Metrics.Completed}   in system {r.Sim.VehiclesInSystem()}";
            }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (!Visible || _p == null) return;
            switch (ev)
            {
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                {
                    if (_state != State.Build) break;
                    var world = App.Net.GetGlobalMousePosition();
                    foreach (var op in _sol.Ops)
                    {
                        if (op.kind != EditKind.Roundabout) continue;
                        var nd = _p.level.network.nodes.Find(n => n.id == op.node);
                        if (nd != null && world.DistanceTo(App.Net.ToWorld(nd.x, nd.y)) < App.Net.Legible(20f * App.Net.PixelsPerMeter, 30f))
                        { _popup.OpenRoundabout(op, _p.level, _applied, _p.toolbox, _sol, _p.initialOps); return; }
                    }
                    if (App.Net.TryPickNode(world, out int nodeId)) OpenNode(nodeId);
                    else if (App.Net.TryPickLink(world, out int linkId)) _popup.OpenLink(linkId, _p.level, _p.toolbox, _sol, _p.initialOps);
                    else _popup.Close();
                    break;
                }
                case InputEventKey { Pressed: true, Echo: false } k:
                    if (_state == State.Running)
                        switch (k.Keycode)
                        {
                            case Key.Space: App.Runner.TimeScale = App.Runner.TimeScale > 0f ? 0f : 2f; break;
                            case Key.Key1: App.Runner.TimeScale = 1f; break;
                            case Key.Key2: App.Runner.TimeScale = 2f; break;
                            case Key.Key4: App.Runner.TimeScale = 4f; break;
                            case Key.Key8: App.Runner.TimeScale = 8f; break;
                        }
                    else if (_state == State.Build && k.Keycode == Key.Enter) Run();
                    break;
            }
        }
    }
}
