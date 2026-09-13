using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// P0: the level player. Loads any built-in level (or a LevelDef JSON via
    /// --level=path.json), fits the camera, runs the player's sim against the
    /// lockstep MaxPressure ghost, and shows a results panel when the round
    /// ends. MaxPressure drives every light; tap an approach to hold it green.
    ///
    /// Keys: space pause · 1/2/4 speed · R restart · [ ] previous/next level
    ///       F refit camera · wheel zoom · middle/right-drag or WASD pan
    /// </summary>
    public partial class Main : Node2D
    {
        private const ulong Seed = 20260817;

        private SimRunner _runner;
        private NetworkView _net;
        private VehicleView _vehicles;
        private CameraRig _cam;

        private Label _title, _hud, _hint;
        private OptionButton _pick;
        private PanelContainer _results;
        private Label _resultsTitle, _resultsText;

        private readonly List<string> _names = new();
        private int _levelIdx;

        // --screenshot dev hook (see _Ready)
        private string _shotPath;
        private float _shotAt = 40f;
        private bool _shotTaken;

        public override void _Ready()
        {
            _runner = new SimRunner();
            AddChild(_runner);
            _runner.Spillback += linkId => _net.FlashSpillback(linkId);
            _runner.FinishedRound += OnFinishedRound;

            _net = new NetworkView { Runner = _runner };
            AddChild(_net);
            _vehicles = new VehicleView { Runner = _runner, Net = _net };
            AddChild(_vehicles);

            _cam = new CameraRig();
            AddChild(_cam);
            _cam.MakeCurrent();

            BuildHud();
            BuildResults();

            _names.AddRange(Levels.Names);
            // Dev hook: --screenshot=path [--after=simSeconds] renders a level at
            // speed, saves a PNG, and quits. Used for visual checks without a display.
            _shotPath = Arg("screenshot");
            if (_shotPath != null && float.TryParse(Arg("after"), out var shotAt)) _shotAt = shotAt;
            string want = Arg("level") ?? "sc-couplet";
            int idx = _names.IndexOf(want);
            if (idx < 0)
            {
                // Not a built-in: treat it as a JSON path and append it to the list.
                _names.Add(want);
                idx = _names.Count - 1;
                _pick.AddItem(want);
            }
            LoadLevel(idx);
            if (_shotPath != null) _runner.TimeScale = 8f;   // reach the capture time quickly
        }

        // ------------------------------------------------------------ levels

        /// <summary>User arg "--key=value" or "--key value" (after "--" on the command line).</summary>
        private static string Arg(string key)
        {
            var args = OS.GetCmdlineUserArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--" + key + "=")) return args[i].Substring(key.Length + 3);
                if (args[i] == "--" + key && i + 1 < args.Length) return args[i + 1];
            }
            return null;
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

            _runner.Load(level, Seed);
            _runner.TimeScale = 1f;
            _results.Visible = false;
            _pick.Selected = _levelIdx;
            _title.Text = level.name.ToUpperInvariant();
            FitCamera();
        }

        private void FitCamera()
        {
            var net = _runner.Sim.Network;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var n in net.Nodes)
            {
                var p = _net.ToWorld(n.X, n.Y);
                min = min.Min(p); max = max.Max(p);
            }
            float margin = 40f * _net.PixelsPerMeter;
            var rect = new Rect2(min - new Vector2(margin, margin),
                                 (max - min) + new Vector2(2f * margin, 2f * margin));
            _cam.FitTo(rect);
        }

        // --------------------------------------------------------------- hud

        private StyleBoxFlat PanelStyle()
        {
            var style = new StyleBoxFlat
            {
                BgColor = Orbitope.Surface with { A = 0.92f },
                BorderColor = Orbitope.Border,
                ContentMarginLeft = 12, ContentMarginRight = 12,
                ContentMarginTop = 8, ContentMarginBottom = 8,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            };
            style.SetBorderWidthAll(1);
            return style;
        }

        private Label MakeLabel(FontFile font, int size, Color color, string text = "")
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", font);
            l.AddThemeFontSizeOverride("font_size", size);
            l.AddThemeColorOverride("font_color", color);
            return l;
        }

        private void BuildHud()
        {
            var layer = new CanvasLayer();
            AddChild(layer);

            var panel = new PanelContainer { Position = new Vector2(10, 10) };
            panel.AddThemeStyleboxOverride("panel", PanelStyle());
            layer.AddChild(panel);

            var col = new VBoxContainer();
            panel.AddChild(col);

            var top = new HBoxContainer();
            col.AddChild(top);
            _title = MakeLabel(Orbitope.Rajdhani, 20, Orbitope.TextBright, "SIGNAL");
            top.AddChild(_title);

            _pick = new OptionButton();
            _pick.AddThemeFontOverride("font", Orbitope.Mono);
            _pick.AddThemeFontSizeOverride("font_size", 12);
            foreach (var n in Levels.Names) _pick.AddItem(n);
            _pick.ItemSelected += idx => LoadLevel((int)idx);
            top.AddChild(_pick);

            _hud = MakeLabel(Orbitope.Mono, 13, Orbitope.TextPrimary);
            col.AddChild(_hud);
            _hint = MakeLabel(Orbitope.Mono, 11, Orbitope.TextMuted,
                "tap an approach to hold it green · space pause · 1/2/4 speed · R restart · [ ] level · F fit");
            col.AddChild(_hint);
        }

        private void BuildResults()
        {
            var layer = new CanvasLayer();
            AddChild(layer);

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(center);

            _results = new PanelContainer { Visible = false };
            _results.AddThemeStyleboxOverride("panel", PanelStyle());
            center.AddChild(_results);

            var col = new VBoxContainer();
            _results.AddChild(col);

            _resultsTitle = MakeLabel(Orbitope.Rajdhani, 24, Orbitope.TextBright, "ROUND OVER");
            col.AddChild(_resultsTitle);
            _resultsText = MakeLabel(Orbitope.Mono, 13, Orbitope.TextPrimary);
            col.AddChild(_resultsText);

            var row = new HBoxContainer();
            col.AddChild(row);
            var restart = new Button { Text = "Restart (R)" };
            restart.Pressed += Restart;
            row.AddChild(restart);
            var next = new Button { Text = "Next level (])" };
            next.Pressed += () => LoadLevel(_levelIdx + 1);
            row.AddChild(next);
        }

        // ------------------------------------------------------------ round

        private void Restart()
        {
            _runner.Reset();
            _runner.TimeScale = 1f;
            _results.Visible = false;
        }

        private void OnFinishedRound()
        {
            var you = _runner.Sim.Metrics;
            var ai = _runner.Ghost.Metrics;
            float yw = you.LiveAvgWait(_runner.Sim), aw = ai.LiveAvgWait(_runner.Ghost);
            bool win = yw <= aw;
            _resultsTitle.Text = win ? "YOU BEAT THE AI" : "THE AI WINS";
            _resultsTitle.AddThemeColorOverride("font_color", win ? Orbitope.AmberBright : Orbitope.TextSecondary);
            _resultsText.Text =
                $"{_runner.Level.name}  ·  {_runner.Level.duration:F0} s\n\n" +
                $"                you      AI\n" +
                $"avg wait     {yw,6:F1}s  {aw,6:F1}s\n" +
                $"cars done    {you.Completed,6}   {ai.Completed,6}\n" +
                $"worst wait   {you.MaxWait,6:F0}s  {ai.MaxWait,6:F0}s\n" +
                $"spillbacks   {you.SpillbackEvents,6}   {ai.SpillbackEvents,6}";
            _results.Visible = true;
        }

        public override void _Process(double delta)
        {
            if (_runner.Sim == null) return;
            if (_shotPath != null && !_shotTaken && _runner.Sim.Time >= _shotAt)
            {
                _shotTaken = true;
                _ = CaptureAndQuit(_shotPath);
            }
            float you = _runner.Sim.Metrics.LiveAvgWait(_runner.Sim);
            float ghost = _runner.Ghost.Metrics.LiveAvgWait(_runner.Ghost);
            string lead = you <= ghost ? "you lead" : "AI leads";
            _title.AddThemeColorOverride("font_color",
                you <= ghost ? Orbitope.TextBright : Orbitope.TextSecondary);
            string speed = _runner.TimeScale <= 0f ? "paused" : $"{_runner.TimeScale:F0}x";
            _hud.Text = $"t {_runner.Sim.Time,5:F0}s / {_runner.Level.duration:F0}s   {speed}   {_runner.SignalCount} signals\n" +
                        $"avg wait  you {you,5:F1}s   AI {ghost,5:F1}s   ({lead})\n" +
                        $"in system {_runner.Sim.VehiclesInSystem()}   done {_runner.Sim.Metrics.Completed}   " +
                        $"spillbacks {_runner.Sim.Metrics.SpillbackEvents}";
        }

        private async System.Threading.Tasks.Task CaptureAndQuit(string path)
        {
            _runner.TimeScale = 0f;
            // Let one more frame render with the sim frozen, then grab it.
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = GetViewport().GetTexture().GetImage();
            var err = img.SavePng(path);
            GD.Print(err == Error.Ok ? $"screenshot saved: {path}" : $"screenshot FAILED ({err}): {path}");
            GetTree().Quit(err == Error.Ok ? 0 : 1);
        }

        // ------------------------------------------------------------ input

        public override void _UnhandledInput(InputEvent ev)
        {
            switch (ev)
            {
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                {
                    if (_results.Visible) break;
                    var world = _net.GetGlobalMousePosition();
                    if (_net.TryPickApproach(world, out int nodeId, out int inLink))
                        _runner.RequestGreenFor(nodeId, inLink);
                    break;
                }
                case InputEventKey { Pressed: true, Echo: false } k:
                    switch (k.Keycode)
                    {
                        case Key.Space: _runner.TimeScale = _runner.TimeScale > 0f ? 0f : 1f; break;
                        case Key.Key1: _runner.TimeScale = 1f; break;
                        case Key.Key2: _runner.TimeScale = 2f; break;
                        case Key.Key4: _runner.TimeScale = 4f; break;
                        case Key.R: Restart(); break;
                        case Key.Bracketleft: LoadLevel(_levelIdx - 1); break;
                        case Key.Bracketright: LoadLevel(_levelIdx + 1); break;
                    }
                    break;
            }
        }
    }
}
