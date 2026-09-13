using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace SignalGodot
{
    /// <summary>Stars per puzzle and UI settings, in user://. Tiny and forgiving:
    /// a missing or corrupt file just means a fresh start.</summary>
    public static class Progress
    {
        private const string ProgressPath = "user://progress.json";
        private const string SettingsPath = "user://settings.json";

        public static Dictionary<string, int> Stars { get; private set; } = new();
        public static float UiScale = 1f;

        public static void Load()
        {
            try
            {
                if (FileAccess.FileExists(ProgressPath))
                    using (var f = FileAccess.Open(ProgressPath, FileAccess.ModeFlags.Read))
                        Stars = JsonSerializer.Deserialize<Dictionary<string, int>>(f.GetAsText()) ?? new();
                if (FileAccess.FileExists(SettingsPath))
                    using (var f = FileAccess.Open(SettingsPath, FileAccess.ModeFlags.Read))
                    {
                        var s = JsonSerializer.Deserialize<Dictionary<string, float>>(f.GetAsText());
                        if (s != null && s.TryGetValue("uiScale", out var sc)) UiScale = Mathf.Clamp(sc, 0.6f, 2.5f);
                    }
            }
            catch (System.Exception e) { GD.PushWarning($"progress load failed: {e.Message}"); }
        }

        public static int StarsFor(string puzzleId) => Stars.TryGetValue(puzzleId, out var s) ? s : 0;

        public static int TotalStars()
        {
            int n = 0;
            foreach (var w in Signal.Core.Worlds.All) foreach (var p in w.puzzles) n += StarsFor(p.id);
            return n;
        }

        public static bool Unlocked(Signal.Core.WorldDef w) => TotalStars() >= w.unlockStars;

        /// <summary>Record a result; only ever improves.</summary>
        public static bool Record(string puzzleId, int stars)
        {
            if (stars <= StarsFor(puzzleId)) return false;
            Stars[puzzleId] = stars;
            SaveProgress();
            return true;
        }

        public static void SaveProgress()
        {
            try
            {
                using var f = FileAccess.Open(ProgressPath, FileAccess.ModeFlags.Write);
                f.StoreString(JsonSerializer.Serialize(Stars));
            }
            catch (System.Exception e) { GD.PushWarning($"progress save failed: {e.Message}"); }
        }

        public static void SaveSettings()
        {
            try
            {
                using var f = FileAccess.Open(SettingsPath, FileAccess.ModeFlags.Write);
                f.StoreString(JsonSerializer.Serialize(new Dictionary<string, float> { ["uiScale"] = UiScale }));
            }
            catch (System.Exception e) { GD.PushWarning($"settings save failed: {e.Message}"); }
        }
    }
}
