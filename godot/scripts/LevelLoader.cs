using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// Resolves a level spec to a LevelDef. A spec is either a built-in name
    /// from Signal.Core.Levels ("sc-couplet", "corridor", ...) or a path to a
    /// LevelDef JSON file (optionally "file:" prefixed, res:// allowed). JSON
    /// lives here rather than in Core, which stays zero-dependency; the options
    /// mirror Signal.Headless so both read the same files.
    /// </summary>
    public static class LevelLoader
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static LevelDef Load(string spec)
        {
            if (spec.StartsWith("file:")) spec = spec.Substring(5);
            if (spec.EndsWith(".json"))
            {
                string path = spec.StartsWith("res://") || spec.StartsWith("user://")
                    ? Godot.ProjectSettings.GlobalizePath(spec) : spec;
                var def = JsonSerializer.Deserialize<LevelDef>(File.ReadAllText(path), Json);
                if (def.name == "unnamed") def.name = Path.GetFileNameWithoutExtension(path);
                return def;
            }
            return Levels.Get(spec);
        }

        public static string Save(LevelDef level, string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(level,
                new JsonSerializerOptions(Json) { WriteIndented = true }));
            return path;
        }
    }
}
