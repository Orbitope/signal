using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// Build-and-run puzzle screen (GAME_PLAN P1). Left: the brief, objectives,
    /// money, your changes, Run. Map: click a junction or an approach to get
    /// the tools that apply there. Run scores every seed instantly and then
    /// plays seed 0 on screen, so what you watch is what was scored.
    /// </summary>
    public partial class PuzzlePlay : CanvasLayer
    {
        public Main App;

        private const float PanelWidth = 390f;
        private const int WrapWidth = (int)PanelWidth - 40;

        private enum State { Build, Running, Done }

        private PuzzleDef _p;
        private Solution _sol = new();
        private LevelDef _applied;
        private PuzzleResult _result;
        private State _state;
        private int _fails;
        private bool _showHint, _built;

        // Side panel widgets.
        private Label _title, _sub, _intro, _hintLabel, _budget, _error, _status, _verdict;
        private VBoxContainer _objRows, _opsRows;
        private HBoxContainer _buildButtons, _runButtons, _doneButtons;
        private Button _run, _reveal;
        private readonly List<Label> _objLabels = new();

        // Tool popup.
        private PanelContainer _popup;
        private VBoxContainer _popupCol;

        // ------------------------------------------------------------ lifecycle

        public void Enter(PuzzleDef p)
        {
            if (!_built) Build();
            _p = p;
            _sol = Solution.From(p.initialOps);
            _fails = 0; _showHint = false; _result = null;
            _state = State.Build;
            App.Runner.FinishedRound += OnFinishedRound;
            App.Net.ShowCompass = true;
            _title.Text = p.title.ToUpperInvariant();
            _sub.Text = $"{Worlds.All[0].title} · puzzle {IndexOf(p) + 1}";
            _intro.Text = p.intro;
            Rebuild();
            ClosePopup();
            RefreshPanel();
            SetProcessUnhandledInput(true);
        }

        public void Exit()
        {
            App.Runner.FinishedRound -= OnFinishedRound;
            App.Runner.TimeScale = 0f;
            App.Net.SelectedNode = -1; App.Net.SelectedLink = -1; App.Net.SelectedPoint = null;
            ClosePopup();
            SetProcessUnhandledInput(false);
        }

        private static int IndexOf(PuzzleDef p) => Worlds.All[0].puzzles.IndexOf(p);

        // ------------------------------------------------------------ model

        /// <summary>Apply the current ops, load the result paused. Returns an error message or null.</summary>
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
            if (err != null)
            {
                _sol = backup;
                Rebuild();
                ShowError("Can't do that: " + err + ".");
            }
            else ShowError(null);
            ClosePopup();
            RefreshPanel();
        }

        private void RemoveOp(EditOp op)
        {
            _sol.Remove(op);
            string err = Rebuild();
            if (err != null) ShowError("Can't do that: " + err + ".");
            ClosePopup();
            RefreshPanel();
        }

        /// <summary>Dev hook (--popup=node | --popup=link:ID): open a tool popup for a screenshot.</summary>
        public void DebugOpenPopup(string spec)
        {
            if (spec == "node")
            {
                foreach (var n in App.Runner.Sim.Network.Nodes) if (NetworkView.IsEditable(n)) { OpenNodePopup(n.Id); return; }
                foreach (var op in _sol.Ops) if (op.kind == EditKind.Roundabout) { OpenRoundaboutPopup(op); return; }
            }
            else if (spec.StartsWith("link:") && int.TryParse(spec.Substring(5), out int id)) OpenLinkPopup(id);
            else if (spec == "retime")
            {
                var tool = _p.toolbox.Find(t => t.kind == EditKind.Retime);
                if (tool != null) OpenRetimeEditor(NetworkBuilder.Center, tool);
            }
        }

        public void LoadAnswer()
        {
            if (_p?.answer == null) return;
            _sol = Solution.From(_p.answer);
            Rebuild();
            ClosePopup();
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
            ClosePopup();
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
            var list = Worlds.All[0].puzzles;
            int i = IndexOf(_p);
            if (i + 1 < list.Count) App.ShowPuzzle(list[i + 1]);
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
            _buildButtons.AddChild(Ui.Button("Reset", () => { _sol = Solution.From(_p.initialOps); Rebuild(); ClosePopup(); RefreshPanel(); }));
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

            // Popup host: a full-rect control that ignores the mouse, with the popup pinned top-right.
            var host = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(host);
            _popup = Ui.Panel();
            _popup.Visible = false;
            _popup.CustomMinimumSize = new Vector2(360, 0);
            _popup.AnchorLeft = 1f; _popup.AnchorRight = 1f;
            _popup.OffsetLeft = -376f; _popup.OffsetRight = -16f; _popup.OffsetTop = 16f;
            _popup.GrowHorizontal = Control.GrowDirection.Begin;
            _popup.GrowVertical = Control.GrowDirection.End;
            host.AddChild(_popup);
            _popupCol = Ui.Column(6);
            _popup.AddChild(_popupCol);
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

            // Objectives.
            foreach (var c in _objRows.GetChildren()) c.QueueFree();
            _objLabels.Clear();
            for (int i = 0; i < _p.objectives.Count; i++)
            {
                var l = Ui.Label("", Ui.Body, Orbitope.TextPrimary, null, wrap: true, width: WrapWidth);
                _objRows.AddChild(l);
                _objLabels.Add(l);
            }
            UpdateObjectiveRows();

            // Money.
            int cost = Cost();
            int left = _p.budget - cost;
            _budget.Text = $"Budget ${_p.budget}  spent ${cost}  left ${left}\n(three stars at ${_p.par} or less)";
            _budget.AddThemeColorOverride("font_color", left < 0 ? Ui.Bad : Orbitope.TextPrimary);

            // Changes list.
            foreach (var c in _opsRows.GetChildren()) c.QueueFree();
            if (_sol.Ops.Count == 0)
                _opsRows.AddChild(Ui.Label("none yet", Ui.Body, Orbitope.TextMuted));
            foreach (var op in _sol.Ops)
            {
                var row = Ui.Row(8);
                bool given = _p.initialOps.Exists(i => i.SameAs(op));
                var tool = _p.ToolFor(op);
                string price = given ? "given" : tool != null ? $"${tool.price}" : "?";
                var l = Ui.Label($"{Describe(op)}  ({price})", Ui.Body, given ? Orbitope.TextSecondary : Orbitope.TextPrimary, null, wrap: true, width: WrapWidth - 60);
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

            // State-dependent buttons and verdict.
            _buildButtons.Visible = _state == State.Build;
            _runButtons.Visible = _state == State.Running;
            _doneButtons.Visible = _state == State.Done;
            _reveal.Visible = _state == State.Build && _fails >= 2 && _p.answer != null && _p.answer.Count >= 0;
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

        // ------------------------------------------------------------ naming

        private static string ControlName(ControlType c, int axis = 0) => c switch
        {
            ControlType.Signalized => "Traffic signal",
            ControlType.AllWayStop => "All-way stop",
            ControlType.TwoWayStop => axis == 0 ? "Two-way stop (E-W keeps priority)" : "Two-way stop (N-S keeps priority)",
            ControlType.YieldEntry => "Yield",
            _ => "Uncontrolled"
        };

        private string ApproachName(int linkId)
        {
            var net = _p.level.network;
            var link = net.links.Find(l => l.id == linkId);
            int hops = 0;
            while (link != null && hops++ < 4)
            {
                var from = net.nodes.Find(n => n.id == link.from);
                if (from == null) break;
                if (from.isBoundary) return "from " + Edits.NodeName(_p.level, from.id);
                var up = net.links.Find(l => l.to == from.id);
                link = up;
            }
            return $"street {linkId}";
        }

        private string Describe(EditOp op) => op.kind switch
        {
            EditKind.SetControl => ControlName(op.control, op.majorAxis),
            EditKind.Roundabout => "Roundabout",
            EditKind.Retime => op.splits != null && op.splits.Count == 2
                ? $"Timed plan: {op.cycle:F0} s cycle, N-S {op.splits[0] * 100f:F0}% / E-W {op.splits[1] * 100f:F0}%"
                : $"Timed plan: {op.cycle:F0} s cycle, even split",
            EditKind.AddBay => $"Left-turn bay {ApproachName(op.link)}",
            EditKind.TurnBan => $"No left turn {ApproachName(op.link)}",
            EditKind.OneWay => $"One-way: closed the lane {ApproachName(op.link)}",
            _ => op.kind.ToString()
        };

        private string CurrentControlName(int nodeId)
        {
            var retime = _sol.Find(o => o.kind == EditKind.Retime && o.node == nodeId);
            var nd = _applied.network.nodes.Find(n => n.id == nodeId);
            if (nd == null) return "Roundabout";
            if (nd.control == ControlType.Signalized) return retime != null ? "Traffic signal on a timed plan" : "Traffic signal run by the AI";
            if (nd.control == ControlType.TwoWayStop)
            {
                var op = _sol.Find(o => o.kind == EditKind.SetControl && o.node == nodeId);
                return ControlName(ControlType.TwoWayStop, op?.majorAxis ?? 0);
            }
            return ControlName(nd.control);
        }

        // ------------------------------------------------------------ popup

        private void ClosePopup()
        {
            _popup.Visible = false;
            App.Net.SelectedNode = -1; App.Net.SelectedLink = -1; App.Net.SelectedPoint = null;
        }

        private void OpenPopup(string title, string subtitle)
        {
            foreach (var c in _popupCol.GetChildren()) c.QueueFree();
            _popupCol.AddChild(Ui.Label(title, Ui.Heading, Orbitope.TextBright, Orbitope.Rajdhani));
            if (!string.IsNullOrEmpty(subtitle))
                _popupCol.AddChild(Ui.Label(subtitle, Ui.Body, Orbitope.TextSecondary, null, wrap: true, width: 320));
            _popupCol.AddChild(Ui.Separator());
            _popup.Visible = true;
        }

        private Button ToolButton(ToolDef tool, string label, System.Action onPress)
        {
            var b = Ui.Button($"{label}   ${tool.price}", onPress);
            b.TooltipText = tool.blurb;
            _popupCol.AddChild(b);
            return b;
        }

        private void AddBlurb(string text)
            => _popupCol.AddChild(Ui.Label(text, Ui.Small, Orbitope.TextMuted, null, wrap: true, width: 320));

        private void OpenNodePopup(int nodeId)
        {
            App.Net.SelectedNode = nodeId; App.Net.SelectedLink = -1; App.Net.SelectedPoint = null;
            OpenPopup("The junction", "Now: " + CurrentControlName(nodeId));
            AddNodeTools(nodeId);
        }

        private void OpenRoundaboutPopup(EditOp roundabout)
        {
            var nd = _p.level.network.nodes.Find(n => n.id == roundabout.node);
            App.Net.SelectedNode = -1; App.Net.SelectedLink = -1;
            App.Net.SelectedPoint = nd == null ? null : App.Net.ToWorld(nd.x, nd.y);
            OpenPopup("The junction", "Now: Roundabout");
            _popupCol.AddChild(Ui.Button("Remove the roundabout", () => RemoveOp(roundabout)));
            AddNodeTools(roundabout.node);
        }

        private void AddNodeTools(int nodeId)
        {
            var nd = _applied.network.nodes.Find(n => n.id == nodeId);
            foreach (var tool in _p.toolbox)
            {
                switch (tool.kind)
                {
                    case EditKind.SetControl when tool.control == ControlType.TwoWayStop:
                        ToolButton(tool, "Two-way stop: E-W keeps priority", () => TryAddOp(new EditOp { kind = EditKind.SetControl, node = nodeId, control = ControlType.TwoWayStop, majorAxis = 0 }));
                        ToolButton(tool, "Two-way stop: N-S keeps priority", () => TryAddOp(new EditOp { kind = EditKind.SetControl, node = nodeId, control = ControlType.TwoWayStop, majorAxis = 1 }));
                        break;
                    case EditKind.SetControl:
                        ToolButton(tool, tool.label, () => TryAddOp(new EditOp { kind = EditKind.SetControl, node = nodeId, control = tool.control }));
                        break;
                    case EditKind.Roundabout:
                        if (_sol.Find(o => o.kind == EditKind.Roundabout && o.node == nodeId) == null)
                            ToolButton(tool, tool.label, () => TryAddOp(new EditOp { kind = EditKind.Roundabout, node = nodeId }));
                        break;
                    case EditKind.Retime:
                        if (nd != null && nd.control == ControlType.Signalized)
                            ToolButton(tool, tool.label, () => OpenRetimeEditor(nodeId, tool));
                        break;
                }
            }
            var existing = _sol.Find(o => (o.kind == EditKind.SetControl || o.kind == EditKind.Retime) && o.node == nodeId);
            if (existing != null)
            {
                _popupCol.AddChild(Ui.Separator());
                bool given = _p.initialOps.Exists(i => i.SameAs(existing));
                string label = existing.kind == EditKind.Retime ? "Remove the timed plan (the AI runs the light)" : "Undo: " + Describe(existing);
                _popupCol.AddChild(Ui.Button(label + (given ? "" : "  (refund)"), () => RemoveOp(existing)));
            }
            AddBlurb("Hover a tool for what it does.");
        }

        private void OpenRetimeEditor(int nodeId, ToolDef tool)
        {
            var nd = _applied.network.nodes.Find(n => n.id == nodeId);
            int phases = nd?.phases?.Count ?? 2;
            var existing = _sol.Find(o => o.kind == EditKind.Retime && o.node == nodeId);
            float cycle = existing?.cycle ?? 60f;
            float ns = existing?.splits != null && existing.splits.Count == 2 ? existing.splits[0] : 0.5f;

            OpenPopup("Timed plan", $"A fixed cycle instead of the AI.   ${tool.price}");
            var cycleLabel = Ui.Body_($"Cycle length: {cycle:F0} s");
            _popupCol.AddChild(cycleLabel);
            var cycleSlider = new HSlider { MinValue = 30, MaxValue = 120, Step = 10, Value = cycle, CustomMinimumSize = new Vector2(320, 28) };
            cycleSlider.ValueChanged += v => { cycle = (float)v; cycleLabel.Text = $"Cycle length: {cycle:F0} s"; };
            _popupCol.AddChild(cycleSlider);

            HSlider nsSlider = null;
            if (phases == 2)
            {
                var nsLabel = Ui.Body_($"Green share: N-S {ns * 100f:F0}%  ·  E-W {(1f - ns) * 100f:F0}%");
                _popupCol.AddChild(nsLabel);
                nsSlider = new HSlider { MinValue = 10, MaxValue = 90, Step = 5, Value = ns * 100f, CustomMinimumSize = new Vector2(320, 28) };
                nsSlider.ValueChanged += v => { ns = (float)v / 100f; nsLabel.Text = $"Green share: N-S {ns * 100f:F0}%  ·  E-W {(1f - ns) * 100f:F0}%"; };
                _popupCol.AddChild(nsSlider);
            }
            else AddBlurb($"{phases} phases, split evenly.");

            var row = Ui.Row(8);
            row.AddChild(Ui.Button("Apply", () =>
            {
                var op = new EditOp { kind = EditKind.Retime, node = nodeId, cycle = cycle };
                if (phases == 2) op.splits = new List<float> { ns, 1f - ns };
                TryAddOp(op);
            }, primary: true));
            row.AddChild(Ui.Button("Cancel", ClosePopup));
            _popupCol.AddChild(row);
        }

        private void OpenLinkPopup(int linkId)
        {
            App.Net.SelectedLink = linkId; App.Net.SelectedNode = -1; App.Net.SelectedPoint = null;
            OpenPopup("The approach " + ApproachName(linkId), "");
            bool any = false;
            foreach (var tool in _p.toolbox)
            {
                switch (tool.kind)
                {
                    case EditKind.AddBay:
                        ToolButton(tool, tool.label, () => TryAddOp(new EditOp { kind = EditKind.AddBay, link = linkId })); any = true; break;
                    case EditKind.TurnBan:
                        ToolButton(tool, tool.label, () => TryAddOp(new EditOp { kind = EditKind.TurnBan, link = linkId, turns = TurnMask.Through | TurnMask.Right })); any = true; break;
                    case EditKind.OneWay:
                        ToolButton(tool, tool.label, () => TryAddOp(new EditOp { kind = EditKind.OneWay, link = linkId })); any = true; break;
                }
            }
            if (!any) AddBlurb("No tools for a street in this puzzle. Click the junction itself.");
            var existing = _sol.Find(o => (o.kind == EditKind.AddBay || o.kind == EditKind.TurnBan || o.kind == EditKind.OneWay) && o.link == linkId);
            if (existing != null)
            {
                _popupCol.AddChild(Ui.Separator());
                bool given = _p.initialOps.Exists(i => i.SameAs(existing));
                _popupCol.AddChild(Ui.Button("Undo: " + Describe(existing) + (given ? "" : "  (refund)"), () => RemoveOp(existing)));
            }
        }

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
                    // A roundabout replaced its junction; its original centre stays clickable.
                    foreach (var op in _sol.Ops)
                    {
                        if (op.kind != EditKind.Roundabout) continue;
                        var nd = _p.level.network.nodes.Find(n => n.id == op.node);
                        if (nd != null && world.DistanceTo(App.Net.ToWorld(nd.x, nd.y)) < App.Net.Legible(20f * App.Net.PixelsPerMeter, 30f))
                        { OpenRoundaboutPopup(op); return; }
                    }
                    if (App.Net.TryPickNode(world, out int nodeId)) OpenNodePopup(nodeId);
                    else if (App.Net.TryPickLink(world, out int linkId)) OpenLinkPopup(linkId);
                    else ClosePopup();
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
