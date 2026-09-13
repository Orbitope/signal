using Godot;
using System;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// The tool popup shared by puzzles and the editor: pinned top-right of
    /// the map, lists the priced tools that apply to a junction or an
    /// approach, an undo for whatever is already there, and the timed-plan
    /// editor. The owner supplies the toolbox, the working solution and
    /// callbacks; the popup never mutates anything itself.
    /// </summary>
    public partial class ToolPopup : PanelContainer
    {
        public NetworkView Net;
        public Action<EditOp> OnAdd, OnRemove;
        public bool ShowPrices = true;

        private VBoxContainer _col;

        public override void _Ready()
        {
            AddThemeStyleboxOverride("panel", Ui.PanelStyle());
            Visible = false;
            CustomMinimumSize = new Vector2(360, 0);
            AnchorLeft = 1f; AnchorRight = 1f;
            OffsetLeft = -376f; OffsetRight = -16f; OffsetTop = 16f;
            GrowHorizontal = Control.GrowDirection.Begin;
            GrowVertical = Control.GrowDirection.End;
            _col = Ui.Column(6);
            AddChild(_col);
        }

        public void Close()
        {
            Visible = false;
            if (Net != null) { Net.SelectedNode = -1; Net.SelectedLink = -1; Net.SelectedPoint = null; }
        }

        private void Begin(string title, string subtitle)
        {
            foreach (var c in _col.GetChildren()) c.QueueFree();
            _col.AddChild(Ui.Label(title, Ui.Heading, Orbitope.TextBright, Orbitope.Rajdhani));
            if (!string.IsNullOrEmpty(subtitle))
                _col.AddChild(Ui.Label(subtitle, Ui.Body, Orbitope.TextSecondary, null, wrap: true, width: 320));
            _col.AddChild(Ui.Separator());
            Visible = true;
        }

        private Button ToolButton(ToolDef tool, string label, Action onPress)
        {
            var b = Ui.Button(ShowPrices ? $"{label}   ${tool.price}" : label, onPress);
            b.TooltipText = tool.blurb;
            _col.AddChild(b);
            return b;
        }

        private void Blurb(string text)
            => _col.AddChild(Ui.Label(text, Ui.Small, Orbitope.TextMuted, null, wrap: true, width: 320));

        private static bool IsGiven(IList<EditOp> initial, EditOp op)
        {
            if (initial == null) return false;
            foreach (var i in initial) if (i.SameAs(op)) return true;
            return false;
        }

        // ------------------------------------------------------------ junction

        public void OpenNode(int nodeId, string currentName, LevelDef baseLevel, LevelDef applied,
                             IList<ToolDef> toolbox, Solution sol, IList<EditOp> initial, Action<VBoxContainer> extra = null)
        {
            if (Net != null) { Net.SelectedNode = nodeId; Net.SelectedLink = -1; Net.SelectedPoint = null; }
            Begin("The junction", currentName == null ? "" : "Now: " + currentName);
            extra?.Invoke(_col);
            AddNodeTools(nodeId, baseLevel, applied, toolbox, sol, initial);
        }

        public void OpenRoundabout(EditOp roundabout, LevelDef baseLevel, LevelDef applied,
                                   IList<ToolDef> toolbox, Solution sol, IList<EditOp> initial, Action<VBoxContainer> extra = null)
        {
            var nd = baseLevel.network.nodes.Find(n => n.id == roundabout.node);
            if (Net != null)
            {
                Net.SelectedNode = -1; Net.SelectedLink = -1;
                Net.SelectedPoint = nd == null ? null : Net.ToWorld(nd.x, nd.y);
            }
            Begin("The junction", "Now: Roundabout");
            extra?.Invoke(_col);
            _col.AddChild(Ui.Button("Remove the roundabout", () => OnRemove?.Invoke(roundabout)));
            AddNodeTools(roundabout.node, baseLevel, applied, toolbox, sol, initial);
        }

        private void AddNodeTools(int nodeId, LevelDef baseLevel, LevelDef applied, IList<ToolDef> toolbox, Solution sol, IList<EditOp> initial)
        {
            var nd = applied?.network.nodes.Find(n => n.id == nodeId);
            foreach (var tool in toolbox)
            {
                switch (tool.kind)
                {
                    case EditKind.SetControl when tool.control == ControlType.TwoWayStop:
                        ToolButton(tool, "Two-way stop: E-W keeps priority", () => OnAdd?.Invoke(new EditOp { kind = EditKind.SetControl, node = nodeId, control = ControlType.TwoWayStop, majorAxis = 0 }));
                        ToolButton(tool, "Two-way stop: N-S keeps priority", () => OnAdd?.Invoke(new EditOp { kind = EditKind.SetControl, node = nodeId, control = ControlType.TwoWayStop, majorAxis = 1 }));
                        break;
                    case EditKind.SetControl:
                        ToolButton(tool, tool.label, () => OnAdd?.Invoke(new EditOp { kind = EditKind.SetControl, node = nodeId, control = tool.control }));
                        break;
                    case EditKind.Roundabout:
                        if (sol.Find(o => o.kind == EditKind.Roundabout && o.node == nodeId) == null)
                            ToolButton(tool, tool.label, () => OnAdd?.Invoke(new EditOp { kind = EditKind.Roundabout, node = nodeId }));
                        break;
                    case EditKind.Retime:
                        if (nd != null && nd.control == ControlType.Signalized)
                            ToolButton(tool, tool.label, () => OpenRetimeEditor(nodeId, tool, applied, sol));
                        break;
                }
            }
            var existing = sol.Find(o => (o.kind == EditKind.SetControl || o.kind == EditKind.Retime) && o.node == nodeId);
            if (existing != null)
            {
                _col.AddChild(Ui.Separator());
                bool given = IsGiven(initial, existing);
                string label = existing.kind == EditKind.Retime ? "Remove the timed plan (the AI runs the light)" : "Undo: " + Edits.Describe(existing, baseLevel);
                _col.AddChild(Ui.Button(label + (given || !ShowPrices ? "" : "  (refund)"), () => OnRemove?.Invoke(existing)));
            }
            Blurb("Hover a tool for what it does.");
        }

        private void OpenRetimeEditor(int nodeId, ToolDef tool, LevelDef applied, Solution sol)
        {
            var nd = applied?.network.nodes.Find(n => n.id == nodeId);
            int phases = nd?.phases?.Count ?? 2;
            var existing = sol.Find(o => o.kind == EditKind.Retime && o.node == nodeId);
            float cycle = existing?.cycle ?? 60f;
            float ns = existing?.splits != null && existing.splits.Count == 2 ? existing.splits[0] : 0.5f;

            Begin("Timed plan", ShowPrices ? $"A fixed cycle instead of the AI.   ${tool.price}" : "A fixed cycle instead of the AI.");
            var cycleLabel = Ui.Body_($"Cycle length: {cycle:F0} s");
            _col.AddChild(cycleLabel);
            var cycleSlider = new HSlider { MinValue = 30, MaxValue = 120, Step = 10, Value = cycle, CustomMinimumSize = new Vector2(320, 28) };
            cycleSlider.ValueChanged += v => { cycle = (float)v; cycleLabel.Text = $"Cycle length: {cycle:F0} s"; };
            _col.AddChild(cycleSlider);

            if (phases == 2)
            {
                var nsLabel = Ui.Body_($"Green share: N-S {ns * 100f:F0}%  ·  E-W {(1f - ns) * 100f:F0}%");
                _col.AddChild(nsLabel);
                var nsSlider = new HSlider { MinValue = 10, MaxValue = 90, Step = 5, Value = ns * 100f, CustomMinimumSize = new Vector2(320, 28) };
                nsSlider.ValueChanged += v => { ns = (float)v / 100f; nsLabel.Text = $"Green share: N-S {ns * 100f:F0}%  ·  E-W {(1f - ns) * 100f:F0}%"; };
                _col.AddChild(nsSlider);
            }
            else Blurb($"{phases} phases, split evenly.");

            var row = Ui.Row(8);
            row.AddChild(Ui.Button("Apply", () =>
            {
                var op = new EditOp { kind = EditKind.Retime, node = nodeId, cycle = cycle };
                if (phases == 2) op.splits = new List<float> { ns, 1f - ns };
                OnAdd?.Invoke(op);
            }, primary: true));
            row.AddChild(Ui.Button("Cancel", Close));
            _col.AddChild(row);
        }

        // ------------------------------------------------------------ approach

        public void OpenLink(int linkId, LevelDef baseLevel, IList<ToolDef> toolbox, Solution sol, IList<EditOp> initial, Action<VBoxContainer> extra = null)
        {
            if (Net != null) { Net.SelectedLink = linkId; Net.SelectedNode = -1; Net.SelectedPoint = null; }
            Begin("The approach " + Edits.ApproachName(baseLevel, linkId), "");
            extra?.Invoke(_col);
            bool any = false;
            foreach (var tool in toolbox)
            {
                switch (tool.kind)
                {
                    case EditKind.AddBay:
                        ToolButton(tool, tool.label, () => OnAdd?.Invoke(new EditOp { kind = EditKind.AddBay, link = linkId })); any = true; break;
                    case EditKind.TurnBan:
                        ToolButton(tool, tool.label, () => OnAdd?.Invoke(new EditOp { kind = EditKind.TurnBan, link = linkId, turns = TurnMask.Through | TurnMask.Right })); any = true; break;
                    case EditKind.OneWay:
                        ToolButton(tool, tool.label, () => OnAdd?.Invoke(new EditOp { kind = EditKind.OneWay, link = linkId })); any = true; break;
                }
            }
            if (!any && extra == null) Blurb("No tools for a street in this puzzle. Click the junction itself.");
            var existing = sol.Find(o => (o.kind == EditKind.AddBay || o.kind == EditKind.TurnBan || o.kind == EditKind.OneWay) && o.link == linkId);
            if (existing != null)
            {
                _col.AddChild(Ui.Separator());
                bool given = IsGiven(initial, existing);
                _col.AddChild(Ui.Button("Undo: " + Edits.Describe(existing, baseLevel) + (given || !ShowPrices ? "" : "  (refund)"), () => OnRemove?.Invoke(existing)));
            }
        }
    }
}
