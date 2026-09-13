using Godot;
using System;

namespace SignalGodot
{
    /// <summary>
    /// UI construction helpers. Legibility first: large type, tall buttons,
    /// generous padding. Sizes are logical pixels; the whole canvas is scaled
    /// by the window's content scale factor (Main.ApplyScale), so ctrl+/-
    /// grows everything at once instead of one widget at a time.
    /// </summary>
    public static class Ui
    {
        public const int Title = 32, Heading = 22, Body = 17, Small = 14, ButtonText = 17;
        public const int ButtonHeight = 42;

        public static readonly Color Good = new("6FCF7A");
        public static readonly Color Bad = new("FF6B4A");

        public static Label Label(string text, int size, Color color, FontFile font = null, bool wrap = false, int width = 0)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", font ?? Orbitope.Mono);
            l.AddThemeFontSizeOverride("font_size", size);
            l.AddThemeColorOverride("font_color", color);
            if (wrap)
            {
                l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                if (width > 0) l.CustomMinimumSize = new Vector2(width, 0);
            }
            return l;
        }

        public static Label Body_(string text, bool wrap = false, int width = 0)
            => Label(text, Body, Orbitope.TextPrimary, null, wrap, width);

        public static Button Button(string text, Action onPress, bool primary = false, int size = ButtonText)
        {
            var b = new Godot.Button { Text = text };
            b.CustomMinimumSize = new Vector2(0, ButtonHeight);
            b.AddThemeFontOverride("font", Orbitope.Mono);
            b.AddThemeFontSizeOverride("font_size", size);
            var normal = Flat(primary ? Orbitope.Amber : Orbitope.Raised, primary ? Orbitope.AmberBright : Orbitope.Border, 10, 6);
            var hover = Flat(primary ? Orbitope.AmberBright : Orbitope.Border, Orbitope.AmberBright, 10, 6);
            var pressed = Flat(Orbitope.AmberBright, Orbitope.AmberBright, 10, 6);
            var disabled = Flat(Orbitope.Surface, Orbitope.Border, 10, 6);
            b.AddThemeStyleboxOverride("normal", normal);
            b.AddThemeStyleboxOverride("hover", hover);
            b.AddThemeStyleboxOverride("pressed", pressed);
            b.AddThemeStyleboxOverride("disabled", disabled);
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            b.AddThemeColorOverride("font_color", primary ? Orbitope.Void : Orbitope.TextBright);
            b.AddThemeColorOverride("font_hover_color", Orbitope.Void);
            b.AddThemeColorOverride("font_pressed_color", Orbitope.Void);
            b.AddThemeColorOverride("font_disabled_color", Orbitope.TextMuted);
            if (onPress != null) b.Pressed += onPress;
            return b;
        }

        public static StyleBoxFlat Flat(Color bg, Color border, int padX, int padY, float radius = 4f, int borderWidth = 1)
        {
            var s = new StyleBoxFlat
            {
                BgColor = bg, BorderColor = border,
                ContentMarginLeft = padX, ContentMarginRight = padX,
                ContentMarginTop = padY, ContentMarginBottom = padY,
                CornerRadiusTopLeft = (int)radius, CornerRadiusTopRight = (int)radius,
                CornerRadiusBottomLeft = (int)radius, CornerRadiusBottomRight = (int)radius,
            };
            s.SetBorderWidthAll(borderWidth);
            return s;
        }

        public static StyleBoxFlat PanelStyle(float alpha = 0.96f)
            => Flat(Orbitope.Surface with { A = alpha }, Orbitope.Border, 18, 14);

        public static PanelContainer Panel(float alpha = 0.96f)
        {
            var p = new PanelContainer();
            p.AddThemeStyleboxOverride("panel", PanelStyle(alpha));
            return p;
        }

        public static VBoxContainer Column(int separation = 8)
        {
            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", separation);
            return v;
        }

        public static HBoxContainer Row(int separation = 8)
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", separation);
            return h;
        }

        public static Control Spacer(int height)
            => new Control { CustomMinimumSize = new Vector2(0, height) };

        public static HSeparator Separator()
        {
            var s = new HSeparator();
            var style = new StyleBoxLine { Color = Orbitope.Border, Thickness = 1 };
            s.AddThemeStyleboxOverride("separator", style);
            return s;
        }

        public static string Stars(int n) => n <= 0 ? "no stars yet" : new string('*', n) + new string('.', 3 - n);
    }
}
