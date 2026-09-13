using Godot;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// Tactical-mode slice: one signalized four-way, tap approaches for green,
    /// race the lockstep MaxPressure ghost on identical demand. Keys: space
    /// pause, 1/2/4 speed. This is milestone M2 running in Godot.
    /// </summary>
    public partial class Main : Node2D
    {
        private SimRunner _runner;
        private NetworkView _net;
        private VehicleView _vehicles;
        private Label _hud;
        private Camera2D _cam;

        public override void _Ready()
        {
            _runner = new SimRunner();
            AddChild(_runner);

            var level = new LevelDef
            {
                name = "fourway-tactical",
                network = NetworkBuilder.FourWay(ControlType.Signalized),
                demand = NetworkBuilder.SymmetricDemand(26f),
                duration = 180f
            };
            _runner.Load(level, seed: 20260817);

            _net = new NetworkView { Runner = _runner };
            AddChild(_net);
            _vehicles = new VehicleView { Runner = _runner, Net = _net };
            AddChild(_vehicles);

            _cam = new Camera2D { Zoom = new Vector2(1.4f, 1.4f), Position = Vector2.Zero };
            AddChild(_cam);
            _cam.MakeCurrent();

            var hudLayer = new CanvasLayer();
            AddChild(hudLayer);

            // HUD: Surface panel, Rajdhani title, JetBrains Mono stats.
            var panel = new PanelContainer { Position = new Vector2(10, 10) };
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
            panel.AddThemeStyleboxOverride("panel", style);
            hudLayer.AddChild(panel);

            var col = new VBoxContainer();
            panel.AddChild(col);

            _title = new Label { Text = "SIGNAL" };
            _title.AddThemeFontOverride("font", Orbitope.Rajdhani);
            _title.AddThemeFontSizeOverride("font_size", 20);
            _title.AddThemeColorOverride("font_color", Orbitope.TextBright);
            col.AddChild(_title);

            _hud = new Label();
            _hud.AddThemeFontOverride("font", Orbitope.Mono);
            _hud.AddThemeFontSizeOverride("font_size", 13);
            _hud.AddThemeColorOverride("font_color", Orbitope.TextPrimary);
            col.AddChild(_hud);

            _runner.Spillback += OnSpillback;
        }

        private Label _title;

        private void OnSpillback(int linkId) => _net.FlashSpillback(linkId);

        public override void _Process(double delta)
        {
            if (_runner.Sim == null) return;
            float you = _runner.Sim.Metrics.LiveAvgWait(_runner.Sim);
            float ghost = _runner.Ghost.Metrics.LiveAvgWait(_runner.Ghost);
            string lead = you <= ghost ? "you lead" : "AI leads";
            _title.AddThemeColorOverride("font_color",
                you <= ghost ? Orbitope.TextBright : Orbitope.TextSecondary);
            _hud.Text = $"t {_runner.Sim.Time,5:F0}s   speed {_runner.TimeScale:F0}x\n" +
                        $"avg wait  you {you,5:F1}s   AI {ghost,5:F1}s   ({lead})\n" +
                        $"in system {_runner.Sim.VehiclesInSystem()}   done {_runner.Sim.Metrics.Completed}\n" +
                        $"tap an approach for green - space pause, 1/2/4 speed";
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            switch (ev)
            {
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb:
                {
                    var world = _net.GetGlobalTransform().AffineInverse() * _cam.GetGlobalTransform()
                                * ((mb.Position - GetViewportRect().Size / 2f) / _cam.Zoom);
                    // Simpler + correct for a centered, unrotated camera:
                    world = (mb.Position - GetViewportRect().Size / 2f) / _cam.Zoom + _cam.Position;
                    if (_net.TryPickApproach(world, out int nodeId, out int inLink))
                        _runner.RequestGreenFor(nodeId, inLink);
                    break;
                }
                case InputEventKey { Pressed: true } k:
                    _runner.TimeScale = k.Keycode switch
                    {
                        Key.Space => _runner.TimeScale > 0f ? 0f : 1f,
                        Key.Key1 => 1f,
                        Key.Key2 => 2f,
                        Key.Key4 => 4f,
                        _ => _runner.TimeScale
                    };
                    break;
            }
        }
    }
}
