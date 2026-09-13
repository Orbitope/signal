using Godot;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// App shell. Owns the shared world (sim runner, network and vehicle views,
    /// camera) and switches between three modes, each a CanvasLayer:
    /// Menu (home + puzzle list), Sandbox (the P0 level player) and PuzzlePlay.
    ///
    /// Args after "--": --level=NAME opens the sandbox on a level,
    /// --puzzle=ID opens a puzzle (--answer=1 loads the authored answer,
    /// --run=1 presses Run), --screenshot=PATH [--after=SIMSECONDS] saves a
    /// PNG and quits — the visual check hook for a display-less session.
    /// Keys: ctrl+= / ctrl+- scale the whole UI, Escape returns to the menu.
    /// </summary>
    public partial class Main : Node2D
    {
        public SimRunner Runner { get; private set; }
        public NetworkView Net { get; private set; }
        public VehicleView Vehicles { get; private set; }
        public CameraRig Cam { get; private set; }

        private Menu _menu;
        private Sandbox _sandbox;
        private PuzzlePlay _puzzle;
        private Editor _editor;
        private CanvasLayer _active;

        // --screenshot dev hook
        private string _shotPath;
        private float _shotAt = 40f;
        private int _shotFrames;
        private bool _shotTaken;

        public override void _Ready()
        {
            SizeWindowForDisplay();
            Progress.Load();
            ApplyScale(Progress.UiScale, save: false);
            LoadGameAi();

            Runner = new SimRunner();
            AddChild(Runner);
            Net = new NetworkView { Runner = Runner };
            AddChild(Net);
            Vehicles = new VehicleView { Runner = Runner, Net = Net };
            AddChild(Vehicles);
            Cam = new CameraRig();
            AddChild(Cam);
            Cam.MakeCurrent();
            Runner.Spillback += linkId => Net.FlashSpillback(linkId);

            _menu = new Menu { App = this }; AddChild(_menu);
            _sandbox = new Sandbox { App = this }; AddChild(_sandbox);
            _puzzle = new PuzzlePlay { App = this }; AddChild(_puzzle);
            _editor = new Editor { App = this }; AddChild(_editor);
            foreach (var m in new CanvasLayer[] { _menu, _sandbox, _puzzle, _editor }) m.Visible = false;

            _shotPath = Arg("screenshot");
            if (_shotPath != null && float.TryParse(Arg("after"), out var shotAt)) _shotAt = shotAt;

            string puzzleId = Arg("puzzle");
            string level = Arg("level");
            if (puzzleId != null && Worlds.Find(puzzleId) is PuzzleDef p)
            {
                ShowPuzzle(p, Worlds.All[0].puzzles, Worlds.All[0].title);
                if (Arg("answer") == "1") _puzzle.LoadAnswer();
                if (Arg("run") == "1") _puzzle.Run();
                if (Arg("popup") is string popup) _puzzle.DebugOpenPopup(popup);
            }
            else if (Arg("editor") == "1")
            {
                ShowEditor();
                if (Arg("popup") is string popup) _editor.DebugOpen(popup);
            }
            else if (level != null) ShowSandbox(level);
            else ShowMenu();
            if (_shotPath != null && Runner.TimeScale > 0f) Runner.TimeScale = 8f;   // reach the capture time quickly
        }

        /// <summary>The trained policies run the lights when their weights are
        /// present (one for a lone junction, one for networks); otherwise the
        /// aging MaxPressure stand-in. --ai=mp forces the stand-in.</summary>
        public const string JunctionPolicyPath = "res://policies/shared-w1-v4-s0.bin";
        public const string NetworkPolicyPath = "res://policies/shared-grid3-flow-v4-s0.bin";

        public static void LoadGameAi()
        {
            bool learned = (Arg("ai") ?? "learned") == "learned";
            GameAi.Junction = Load(JunctionPolicyPath, learned, out GameAi.JunctionName);
            GameAi.Network = Load(NetworkPolicyPath, learned, out GameAi.NetworkName);
            GD.Print($"game AI: junction = {GameAi.JunctionName}; network = {GameAi.NetworkName}");
        }

        private static System.Func<SignalController, ISignalPolicy> Load(string path, bool learned, out string name)
        {
            if (learned && FileAccess.FileExists(path))
            {
                try
                {
                    var w = PolicyWeights.FromBytes(FileAccess.GetFileAsBytes(path));
                    name = "trained policy " + w.Tag;
                    return ctl => new LearnedPolicy(w);
                }
                catch (System.Exception e) { GD.PushWarning($"policy weights failed to load ({path}): {e.Message}"); }
            }
            name = "aging MaxPressure";
            return ctl => new AgingMaxPressurePolicy();
        }

        // ------------------------------------------------------------ modes

        public void ShowMenu() { _menu.ShowHome(); Switch(_menu); MenuBackdrop(); }
        public void ShowPuzzleList() { _menu.ShowPuzzles(); Switch(_menu); MenuBackdrop(); }

        /// <summary>Behind the menu: a city running on its own, dimmed.</summary>
        private void MenuBackdrop()
        {
            if (Runner.Sim == null || Runner.Level.name != "sc-couplet")
            {
                Runner.Load(Levels.Get("sc-couplet"), 1, null, withGhost: false, withTaps: false);
                FitCamera();
            }
            Runner.TimeScale = 1f;
        }
        public void ShowSandbox(string level = null) { Switch(_sandbox); _sandbox.Enter(level); }
        public void ShowPuzzle(PuzzleDef p, System.Collections.Generic.IList<PuzzleDef> set, string setTitle)
        { Switch(_puzzle); _puzzle.Enter(p, set, setTitle); }
        public void ShowEditor() { Switch(_editor); _editor.Enter(); }

        private void Switch(CanvasLayer to)
        {
            if (_active == to) return;
            if (_active is Sandbox s) s.Exit();
            if (_active is PuzzlePlay pp) pp.Exit();
            if (_active is Editor ed) ed.Exit();
            if (_active != null) _active.Visible = false;
            _active = to;
            _active.Visible = true;
        }

        /// <summary>Fit the camera to the loaded network, leaving insetLeft
        /// logical pixels free on the left for a side panel.</summary>
        public void FitCamera(float insetLeft = 0f)
        {
            if (Runner.Sim == null) return;
            var net = Runner.Sim.Network;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var n in net.Nodes)
            {
                var p = Net.ToWorld(n.X, n.Y);
                min = min.Min(p); max = max.Max(p);
            }
            float margin = 40f * Net.PixelsPerMeter;
            var rect = new Rect2(min - new Vector2(margin, margin),
                                 (max - min) + new Vector2(2f * margin, 2f * margin));
            Cam.InsetLeft = insetLeft;
            Cam.FitTo(rect);
        }

        // ------------------------------------------------------------ display

        /// <summary>
        /// The project's 1280x800 is the logical (base) size; stretch mode
        /// canvas_items scales the canvas to the window. On a HiDPI display the
        /// OS reports a scale > 1, so open the window at base * scale or it
        /// comes up half size (Godot window sizes are physical pixels).
        /// </summary>
        private void SizeWindowForDisplay()
        {
            if (DisplayServer.GetName() == "headless") return;
            float scale = DisplayServer.ScreenGetScale();
            var baseSize = new Vector2I(
                ProjectSettings.GetSetting("display/window/size/viewport_width").AsInt32(),
                ProjectSettings.GetSetting("display/window/size/viewport_height").AsInt32());
            var size = new Vector2I(Mathf.RoundToInt(baseSize.X * scale), Mathf.RoundToInt(baseSize.Y * scale));
            var usable = DisplayServer.ScreenGetUsableRect();
            size = new Vector2I(Mathf.Min(size.X, usable.Size.X), Mathf.Min(size.Y, usable.Size.Y));
            DisplayServer.WindowSetSize(size);
            DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
        }

        /// <summary>Scale the whole canvas (UI and map together).</summary>
        public void ApplyScale(float scale, bool save = true)
        {
            scale = Mathf.Clamp(scale, 0.6f, 2.5f);
            Progress.UiScale = scale;
            GetTree().Root.ContentScaleFactor = scale;
            if (save) Progress.SaveSettings();
            Cam?.Refit();
        }

        /// <summary>User arg "--key=value" or "--key value" (after "--" on the command line).</summary>
        public static string Arg(string key)
        {
            var args = OS.GetCmdlineUserArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--" + key + "=")) return args[i].Substring(key.Length + 3);
                if (args[i] == "--" + key && i + 1 < args.Length) return args[i + 1];
            }
            return null;
        }

        // ------------------------------------------------------------ loop

        public override void _Process(double delta)
        {
            if (_shotPath != null && !_shotTaken && Runner.Sim != null && Runner.Sim.Time >= _shotAt && ++_shotFrames > 6)
            {
                _shotTaken = true;
                _ = CaptureAndQuit(_shotPath);
            }
        }

        private async System.Threading.Tasks.Task CaptureAndQuit(string path)
        {
            Runner.TimeScale = 0f;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = GetViewport().GetTexture().GetImage();
            var err = img.SavePng(path);
            GD.Print(err == Error.Ok ? $"screenshot saved: {path}" : $"screenshot FAILED ({err}): {path}");
            GetTree().Quit(err == Error.Ok ? 0 : 1);
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                bool ctrl = k.CtrlPressed || k.MetaPressed;
                if (ctrl && (k.Keycode == Key.Equal || k.Keycode == Key.Plus || k.Keycode == Key.KpAdd))
                { ApplyScale(Progress.UiScale * 1.1f); GetViewport().SetInputAsHandled(); }
                else if (ctrl && (k.Keycode == Key.Minus || k.Keycode == Key.KpSubtract))
                { ApplyScale(Progress.UiScale / 1.1f); GetViewport().SetInputAsHandled(); }
                else if (ctrl && k.Keycode == Key.Key0)
                { ApplyScale(1f); GetViewport().SetInputAsHandled(); }
                else if (k.Keycode == Key.Escape && _active != _menu)
                { ShowMenu(); GetViewport().SetInputAsHandled(); }
            }
        }
    }
}
