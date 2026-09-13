using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// Registry of built-in levels by name. This is the single list every
    /// consumer (the RL env server, the Godot game, the headless bench) draws
    /// from, so a level name means the same thing everywhere. File-backed
    /// levels ("file:path.json") are handled by consumers, because Core has no
    /// JSON dependency.
    /// </summary>
    public static class Levels
    {
        /// <summary>Every name Get() accepts, in a sensible display order.</summary>
        public static IReadOnlyList<string> Names { get; } = BuildNames();

        private static string[] BuildNames()
        {
            var basic = new[]
            {
                "fourway", "fourway-bays", "fourway-bays2",
                "grid2", "grid3", "grid5",
                "corridor", "corridor-rush", "corridor7",
            };
            var all = new string[basic.Length + Scenarios.Names.Length];
            basic.CopyTo(all, 0);
            Scenarios.Names.CopyTo(all, basic.Length);
            return all;
        }

        public static bool TryGet(string name, out LevelDef level)
        {
            try { level = Get(name); return true; }
            catch (ArgumentException) { level = null; return false; }
        }

        public static LevelDef Get(string name)
        {
            LevelDef def = name switch
            {
                "fourway" => new LevelDef { network = NetworkBuilder.FourWay(ControlType.Signalized),
                                            demand = NetworkBuilder.SymmetricDemand(22f) },
                "fourway-bays" => new LevelDef { network = LaneBuilder.FourWayWithBays(),
                                                 demand = NetworkBuilder.SymmetricDemand(30f) },
                "fourway-bays2" => new LevelDef { network = LaneBuilder.FourWayWithBays(throughLanes: 2),
                                                  demand = NetworkBuilder.SymmetricDemand(40f) },
                "grid2" => new LevelDef { network = GridBuilder.Grid(2), demand = GridBuilder.GridDemand(2, 12f) },
                "grid3" => new LevelDef { network = GridBuilder.Grid(3), demand = GridBuilder.GridDemand(3, 12f) },
                "grid5" => new LevelDef { network = GridBuilder.Grid(5), demand = GridBuilder.GridDemand(5, 12f) },
                // Corridor: real road hierarchy — fast arterials, a one-way side
                // street, demand concentrated on the thoroughfares. 5x3 = 15 signals.
                "corridor" => new LevelDef { network = CorridorBuilder.Corridor(5, 3),
                                             demand = CorridorBuilder.CorridorDemand(5, 3) },
                "corridor-rush" => new LevelDef { network = CorridorBuilder.Corridor(5, 3),
                                                  demand = CorridorBuilder.CorridorDemand(5, 3, rush: true) },
                "corridor7" => new LevelDef { network = CorridorBuilder.Corridor(7, 3),
                                              demand = CorridorBuilder.CorridorDemand(7, 3) },
                _ when name.StartsWith("sc-") => Scenarios.Get(name)
                                                 ?? throw new ArgumentException($"unknown scenario '{name}'"),
                _ => throw new ArgumentException($"unknown level '{name}'")
            };
            if (def.name == "unnamed") def.name = name;
            return def;
        }
    }
}
