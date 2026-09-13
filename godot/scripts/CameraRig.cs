using Godot;

namespace SignalGodot
{
    /// <summary>
    /// Camera for any network size: fit-to-bounds on load, wheel zoom about the
    /// cursor, drag to pan (middle or right button), WASD / arrows to pan, F to
    /// refit. Tap handling lives in Main; this only consumes wheel and drags.
    /// </summary>
    public partial class CameraRig : Camera2D
    {
        [Export] public float MinZoom = 0.2f;
        [Export] public float MaxZoom = 6f;
        [Export] public float ZoomStep = 1.15f;
        [Export] public float PanSpeed = 900f;   // screen px per second

        private Rect2 _fit;
        private bool _hasFit;

        public override void _Ready()
        {
            // The fit depends on the viewport size, so redo it when the window changes.
            GetViewport().SizeChanged += Refit;
        }

        public void FitTo(Rect2 world, float pad = 0.88f)
        {
            _fit = world; _hasFit = true;
            var vp = GetViewportRect().Size;
            float zx = vp.X / Mathf.Max(world.Size.X, 1f);
            float zy = vp.Y / Mathf.Max(world.Size.Y, 1f);
            float z = Mathf.Clamp(Mathf.Min(zx, zy) * pad, MinZoom, MaxZoom);
            Zoom = new Vector2(z, z);
            Position = world.GetCenter();
        }

        public void Refit() { if (_hasFit) FitTo(_fit); }

        public override void _UnhandledInput(InputEvent ev)
        {
            switch (ev)
            {
                case InputEventMouseButton { Pressed: true } mb
                    when mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown:
                {
                    float f = mb.ButtonIndex == MouseButton.WheelUp ? ZoomStep : 1f / ZoomStep;
                    var before = GetGlobalMousePosition();
                    float z = Mathf.Clamp(Zoom.X * f, MinZoom, MaxZoom);
                    Zoom = new Vector2(z, z);
                    var after = GetGlobalMousePosition();
                    Position += before - after;          // keep the point under the cursor fixed
                    GetViewport().SetInputAsHandled();
                    break;
                }
                case InputEventMouseMotion mm
                    when mm.ButtonMask.HasFlag(MouseButtonMask.Middle) || mm.ButtonMask.HasFlag(MouseButtonMask.Right):
                    Position -= mm.Relative / Zoom.X;
                    GetViewport().SetInputAsHandled();
                    break;
                case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F }:
                    Refit();
                    break;
            }
        }

        public override void _Process(double delta)
        {
            var v = Vector2.Zero;
            if (Input.IsKeyPressed(Key.Left) || Input.IsKeyPressed(Key.A)) v.X -= 1f;
            if (Input.IsKeyPressed(Key.Right) || Input.IsKeyPressed(Key.D)) v.X += 1f;
            if (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.W)) v.Y -= 1f;
            if (Input.IsKeyPressed(Key.Down) || Input.IsKeyPressed(Key.S)) v.Y += 1f;
            if (v != Vector2.Zero)
                Position += v.Normalized() * PanSpeed * (float)delta / Zoom.X;
        }
    }
}
