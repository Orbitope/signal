using Godot;

namespace SignalGodot
{
    /// <summary>
    /// Orbitope design tokens (ContentKit Palette B — Charcoal + Amber + Coral).
    /// Semantic mapping for Signal: STEEL = flow, AMBER = friction, CORAL = crisis.
    ///
    /// Rules (from the design system):
    ///  1. Coral appears on exactly ONE element per scene. Here that element is
    ///     spillback. Nothing else gets coral — not stress, not red lights.
    ///  2. Amber = things that change (waits climbing, live readouts, yellow phase).
    ///  3. Steel = structure and calm state (free-flowing vehicles, chrome).
    /// </summary>
    public static class Orbitope
    {
        // Backgrounds
        public static readonly Color Void       = new("111009");   // scene bg, never pure black
        public static readonly Color Surface    = new("1E1C16");   // HUD panels
        public static readonly Color Raised     = new("2C2A22");   // roads
        public static readonly Color Border     = new("3D3A30");   // outlines, grid

        // Text
        public static readonly Color TextBright    = new("EDE8DC");
        public static readonly Color TextPrimary   = new("C8C0AE");
        public static readonly Color TextSecondary = new("9A9484");
        public static readonly Color TextMuted     = new("6A6358");

        // Key colors
        public static readonly Color Amber       = new("C49A3C");
        public static readonly Color AmberBright = new("E8C068");
        public static readonly Color Steel       = new("6B7A8D");
        public static readonly Color SteelBright = new("9AAABB");

        // Accent — ONE element per scene (spillback)
        public static readonly Color Coral       = new("FF5E3A");
        public static readonly Color CoralBright = new("FF8C6E");

        // Data series (charts, in priority order; never coral)
        public static readonly Color Sage  = new("7D9A6A");
        public static readonly Color Mauve = new("9A7AB0");
        public static readonly Color Terra = new("C47A5A");

        // Signal-state semantics within the palette:
        //   green light -> Sage, yellow -> Amber (literally), red -> Terra.
        public static readonly Color LightGreen  = Sage;
        public static readonly Color LightYellow = Amber;
        public static readonly Color LightRed    = Terra;

        public static FontFile Rajdhani  => GD.Load<FontFile>("res://fonts/rajdhani.ttf");
        public static FontFile Mono      => GD.Load<FontFile>("res://fonts/JetBrainsMono-Regular.ttf");
        public static FontFile MonoBold  => GD.Load<FontFile>("res://fonts/JetBrainsMono-Bold.ttf");
    }
}
