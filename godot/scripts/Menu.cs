using Godot;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>Home screen and the puzzle list. Big type, few choices.</summary>
    public partial class Menu : CanvasLayer
    {
        public Main App;

        private Control _home, _puzzles;
        private VBoxContainer _puzzleRows;

        public override void _Ready()
        {
            // Dim backdrop so the (paused) map behind reads as scenery.
            var dim = new ColorRect { Color = Orbitope.Void with { A = 0.7f } };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(dim);

            _home = BuildHome();
            _puzzles = BuildPuzzles();
            AddChild(_home); AddChild(_puzzles);
        }

        public void ShowHome() { _home.Visible = true; _puzzles.Visible = false; }
        public void ShowPuzzles() { RefreshPuzzleRows(); _home.Visible = false; _puzzles.Visible = true; }

        private Control BuildHome()
        {
            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var panel = Ui.Panel();
            center.AddChild(panel);
            var col = Ui.Column(14);
            panel.AddChild(col);
            col.AddChild(Ui.Label("SIGNAL", 64, Orbitope.AmberBright, Orbitope.Rajdhani));
            col.AddChild(Ui.Label("Fix the traffic. Every answer is something the simulator measured.", Ui.Body, Orbitope.TextSecondary));
            col.AddChild(Ui.Spacer(6));
            var b1 = Ui.Button("Puzzles", () => App.ShowPuzzleList(), primary: true, size: 20);
            var b2 = Ui.Button("Sandbox  (drive the lights yourself)", () => App.ShowSandbox(), size: 20);
            var b4 = Ui.Button("Editor  (build a level, export a puzzle)", () => App.ShowEditor(), size: 20);
            var b3 = Ui.Button("Quit", () => GetTree().Quit(), size: 20);
            foreach (var b in new[] { b1, b2, b4, b3 }) { b.CustomMinimumSize = new Vector2(420, 52); col.AddChild(b); }
            col.AddChild(Ui.Spacer(4));
            col.AddChild(Ui.Label("ctrl + / ctrl −  bigger or smaller text", Ui.Small, Orbitope.TextMuted));
            return center;
        }

        private Control BuildPuzzles()
        {
            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var panel = Ui.Panel();
            center.AddChild(panel);
            var col = Ui.Column(10);
            panel.AddChild(col);

            var world = Worlds.All[0];
            col.AddChild(Ui.Label(world.title.ToUpperInvariant(), Ui.Title + 6, Orbitope.TextBright, Orbitope.Rajdhani));
            col.AddChild(Ui.Label(world.blurb, Ui.Body, Orbitope.TextSecondary, null, wrap: true, width: 640));
            col.AddChild(Ui.Separator());
            _puzzleRows = Ui.Column(6);
            col.AddChild(_puzzleRows);
            col.AddChild(Ui.Separator());
            var back = Ui.Button("Back", () => App.ShowMenu());
            col.AddChild(back);
            return center;
        }

        private void RefreshPuzzleRows()
        {
            foreach (var c in _puzzleRows.GetChildren()) c.QueueFree();
            var world = Worlds.All[0];
            AddRows(world.puzzles, world.title);
            var mine = Store.LoadPuzzles();
            if (mine.Count > 0)
            {
                _puzzleRows.AddChild(Ui.Separator());
                _puzzleRows.AddChild(Ui.Label("YOUR PUZZLES", Ui.Small, Orbitope.TextMuted, Orbitope.Rajdhani));
                AddRows(mine, "Your puzzles");
            }
        }

        private void AddRows(System.Collections.Generic.List<PuzzleDef> set, string setTitle)
        {
            for (int i = 0; i < set.Count; i++)
            {
                var p = set[i];
                var row = Ui.Row(14);
                var num = Ui.Label($"{i + 1,2}", Ui.Heading, Orbitope.TextMuted);
                num.CustomMinimumSize = new Vector2(40, 0);
                row.AddChild(num);
                var title = Ui.Label(p.title, Ui.Heading, Orbitope.TextBright);
                title.CustomMinimumSize = new Vector2(360, 0);
                row.AddChild(title);
                int stars = Progress.StarsFor(p.id);
                var star = Ui.Label(Ui.Stars(stars), Ui.Body, stars > 0 ? Orbitope.AmberBright : Orbitope.TextMuted);
                star.CustomMinimumSize = new Vector2(150, 0);
                row.AddChild(star);
                var play = Ui.Button(stars > 0 ? "Play again" : "Play", () => App.ShowPuzzle(p, set, setTitle), primary: stars == 0);
                play.CustomMinimumSize = new Vector2(140, Ui.ButtonHeight);
                row.AddChild(play);
                _puzzleRows.AddChild(row);
            }
        }
    }
}
