using Godot;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>Player-made content in user://: editor documents under
    /// levels/, exported puzzles under puzzles/. JSON with the same options
    /// Signal.Headless uses, so files move between the two freely.</summary>
    public static class Store
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public const string LevelsDir = "user://levels";
        public const string PuzzlesDir = "user://puzzles";

        public static string Slug(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            var s = sb.ToString().Trim('-');
            while (s.Contains("--")) s = s.Replace("--", "-");
            return s.Length == 0 ? "untitled" : s;
        }

        // ------------------------------------------------------------ docs

        public static string SaveDoc(EditorDoc doc)
        {
            DirAccess.MakeDirRecursiveAbsolute(LevelsDir);
            string path = $"{LevelsDir}/{Slug(doc.name)}.json";
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(doc, Json));
            return path;
        }

        public static List<string> DocNames()
        {
            var names = new List<string>();
            var dir = DirAccess.Open(LevelsDir);
            if (dir == null) return names;
            foreach (var file in dir.GetFiles())
                if (file.EndsWith(".json")) names.Add(file.Substring(0, file.Length - 5));
            names.Sort();
            return names;
        }

        public static EditorDoc LoadDoc(string slug)
        {
            string path = $"{LevelsDir}/{slug}.json";
            if (!FileAccess.FileExists(path)) return null;
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            try { return JsonSerializer.Deserialize<EditorDoc>(f.GetAsText(), Json); }
            catch (System.Exception e) { GD.PushWarning($"could not load {path}: {e.Message}"); return null; }
        }

        // ------------------------------------------------------------ puzzles

        public static string SavePuzzle(PuzzleDef p)
        {
            DirAccess.MakeDirRecursiveAbsolute(PuzzlesDir);
            string path = $"{PuzzlesDir}/{Slug(p.id)}.json";
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(p, Json));
            return path;
        }

        public static List<PuzzleDef> LoadPuzzles()
        {
            var list = new List<PuzzleDef>();
            var dir = DirAccess.Open(PuzzlesDir);
            if (dir == null) return list;
            var files = new List<string>(dir.GetFiles());
            files.Sort();
            foreach (var file in files)
            {
                if (!file.EndsWith(".json")) continue;
                try
                {
                    using var f = FileAccess.Open($"{PuzzlesDir}/{file}", FileAccess.ModeFlags.Read);
                    var p = JsonSerializer.Deserialize<PuzzleDef>(f.GetAsText(), Json);
                    if (p?.level != null) list.Add(p);
                }
                catch (System.Exception e) { GD.PushWarning($"could not load puzzle {file}: {e.Message}"); }
            }
            return list;
        }
    }
}
