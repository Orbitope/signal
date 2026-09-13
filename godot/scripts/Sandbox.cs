using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// The P0 level player: any built-in level (or a LevelDef JSON path), the
    /// game AI on every light, tap an approach to hold it green for a while,
    /// and a lockstep AI ghost to beat. Results panel at the end of the round.
    /// Keys: space pause · 1/2/4 speed · R restart · [ ] level · F refit.
    /// </summary>
    public partial class Sandbox : CanvasLayer
    {
        public Main App;

        private const ulong Seed = 20260817;

        private Label _title, _hud, _hint;
        private OptionButton _pick;
        private PanelContainer _results;
        private Label _resultsTitle, _resultsText;

        private readonly List<string> _names = new();
        private int _levelIdx;
        private bool _built;

        public void Enter(string levelName)
        {
            if (!_built) Build();
            App.Runner.FinishedRound += OnFinishedRound;
            App.Net.ShowCompass = true;
            App.Net.SelectedNode = -1; App.Net.SelectedLink = -1; App.Net.SelectedPoint = null;
            string want = levelName ?? (_names.Count > 0 ? _names[_levelIdx] : "sc-couplet");
            int idx = _names.IndexOf(want);
            if (idx < 0)
            {
                _names.Add(want);           // not a built-in: treat it as a JSON path
                idx = _names.Count - 1;
                _pick.AddItem(want);
            }
            LoadLevel(idx);
            SetProcessUnhandledInput(true);
        }

        public void Exit()
        {
            App.Runner.FinishedRound -= OnFinishedRound;
            App.Runner.TimeScale = 0f;
            SetProcessUnhandledInput(false);
        }

        private void Build()
        {
            _built = true;
            _names.AddRange(Levels.Names);

            var panel = Ui.Panel();
            panel.Position = new Vector2(16, 16);
            AddChild(panel);
            var col = Ui.Column(6);
            panel.AddChild(col);

            var top = Ui.Row(12);
            col.AddChild(top);
            _title = Ui.Label("SIGNAL", Ui.Title, Orbitope.TextBright, Orbitope.Rajdhani);
            top.AddChild(_title);
            _pick = new OptionButton();
            _pick.AddThemeFontOverride("font", Orbitope.Mono);
            _pick.AddThemeFontSizeOverride("font_size", Ui.Body);
            foreach (var n in _names) _pick.AddItem(n);
            _pick.ItemSelected += idx => LoadLevel((int)idx);
            top.AddChild(_pick);
            var back = Ui.Button("Menu", () => App.ShowMenu());
            top.AddChild(back);

            _hud = Ui.Label("", Ui.Body, Orbitope.TextPrimary);
            col.AddChild(_hud);
            _hint = Ui.Label("Tap an approach to hold it green · space pause · 1/2/4 speed · R restart · [ ] level · F fit · Esc menu",
                             Ui.Small, Orbitope.TextMuted);
            col.AddChild(_hint);

            // Results overlay.
            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            center.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(center);
            _results = Ui.Panel();
            _results.Visible = false;
            center.AddChild(_results);
            var rcol = Ui.Column(12);
            _results.AddChild(rcol);
            _resultsTitle = Ui.Label("ROUND OVER", Ui.Title + 4, Orbitope.TextBright, Orbitope.Rajdhani);
            rcol.AddChild(_resultsTitle);
            _resultsText = Ui.Label("", Ui.Body, Orbitope.TextPrimary);
            rcol.AddChild(_resultsText);
            var row = Ui.Row(10);
            rcol.AddChild(row);
            row.AddChild(Ui.Button("Restart (R)", Restart, primary: true));
            row.AddChild(Ui.Button("Next level (])", () => LoadLevel(_levelIdx + 1)));
            row.AddChild(Ui.Button("Menu", () => App.ShowMenu()));
        }

        private void LoadLevel(int idx)
        {
            _levelIdx = ((idx % _names.Count) + _names.Count) % _names.Count;
            string name = _names[_levelIdx];
            LevelDef level;
            try { level = LevelLoader.Load(name); }
            catch (System.Exception e)
            {
                GD.PushError($"could not load level '{name}': {e.Message}");
                return;
            }
            App.Runner.Load(level, Seed);
            App.Runner.TimeScale = 1f;
            _results.Visible = false;
            _pick.Selected = _levelIdx;
            _title.Text = level.name.ToUpperInvariant();
            App.FitCamera();
        }

        private void Restart()
        {
            App.Runner.Reset();
            App.Runner.TimeScale = 1f;
            _results.Visible = false;
        }

        private void OnFinishedRound()
        {
            var you = App.Runner.Sim.Metrics;
            var ai = App.Runner.Ghost?.Metrics;
            if (ai == null) return;
            float yw = you.LiveAvgWait(App.Runner.Sim), aw = ai.LiveAvgWait(App.Runner.Ghost);
            bool win = yw <= aw;
            _resultsTitle.Text = win ? "YOU BEAT THE AI" : "THE AI WINS";
            _resultsTitle.AddThemeColorOverride("font_color", win ? Orbitope.AmberBright : Orbitope.TextSecondary);
            _resultsText.Text =
                $"{App.Runner.Level.name}  ·  {App.Runner.Level.duration:F0} s\n\n" +
                $"                you      AI\n" +
                $"avg wait     {yw,6:F1}s  {aw,6:F1}s\n" +
                $"cars done    {you.Completed,6}   {ai.Completed,6}\n" +
                $"worst wait   {you.MaxWait,6:F0}s  {ai.MaxWait,6:F0}s\n" +
                $"spillbacks   {you.SpillbackEvents,6}   {ai.SpillbackEvents,6}";
            _results.Visible = true;
        }

        public override void _Process(double delta)
        {
            if (!Visible || App?.Runner.Sim == null || App.Runner.Ghost == null) return;
            var r = App.Runner;
            float you = r.Sim.Metrics.LiveAvgWait(r.Sim);
            float ghost = r.Ghost.Metrics.LiveAvgWait(r.Ghost);
            string lead = you <= ghost ? "you lead" : "AI leads";
            _title.AddThemeColorOverride("font_color", you <= ghost ? Orbitope.TextBright : Orbitope.TextSecondary);
            string speed = r.TimeScale <= 0f ? "paused" : $"{r.TimeScale:F0}x";
            _hud.Text = $"t {r.Sim.Time,5:F0}s / {r.Level.duration:F0}s   {speed}   {r.SignalCount} signals\n" +
                        $"avg wait  you {you,5:F1}s   AI {ghost,5:F1}s   ({lead})\n" +
                        $"in system {r.Sim.VehiclesInSystem()}   done {r.Sim.Metrics.Completed}   " +
                        $"spillbacks {r.Sim.Metrics.SpillbackEvents}";
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (!Visible) return;
            switch (ev)
            {
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                {
                    if (_results.Visible) break;
                    var world = App.Net.GetGlobalMousePosition();
                    if (App.Net.TryPickApproach(world, out int nodeId, out int inLink))
                        App.Runner.RequestGreenFor(nodeId, inLink);
                    break;
                }
                case InputEventKey { Pressed: true, Echo: false } k:
                    switch (k.Keycode)
                    {
                        case Key.Space: App.Runner.TimeScale = App.Runner.TimeScale > 0f ? 0f : 1f; break;
                        case Key.Key1: App.Runner.TimeScale = 1f; break;
                        case Key.Key2: App.Runner.TimeScale = 2f; break;
                        case Key.Key4: App.Runner.TimeScale = 4f; break;
                        case Key.R: Restart(); break;
                        case Key.Bracketleft: LoadLevel(_levelIdx - 1); break;
                        case Key.Bracketright: LoadLevel(_levelIdx + 1); break;
                    }
                    break;
            }
        }
    }
}
