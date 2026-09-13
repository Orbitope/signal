using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Signal.Core;

// =============================================================================
//  signal-headless — batch evaluation without Unity. This project is the
//  forcing function that keeps UnityEngine out of Signal.Core.
//
//  Usage:
//    signal-headless bench --level levels/fourway.json [--build b.json] [--seeds 5] [--duration 600]
//    signal-headless bench --builtin fourway-signal|fourway-stop|fourway-roundabout|arterial3
//    signal-headless run   --level ... --policy fixed|greedy|maxpressure --seed 42
//    signal-headless hash  --level ... --seed 42 --steps 20000     (CI determinism gate)
// =============================================================================

class Program
{
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        IncludeFields = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("commands: bench | run | hash | export"); return 1; }
        var opt = ParseArgs(args);

        LevelDef level = LoadLevel(opt);
        BuildVariant build = opt.TryGetValue("build", out var bp)
            ? JsonSerializer.Deserialize<BuildVariant>(File.ReadAllText(bp), Json) : null;

        int seeds = opt.TryGetValue("seeds", out var s) ? int.Parse(s) : 5;
        float duration = opt.TryGetValue("duration", out var d) ? float.Parse(d) : level.duration;

        switch (args[0])
        {
            case "bench": return Bench(level, build, seeds, duration);
            case "run":
            {
                ulong seed = opt.TryGetValue("seed", out var sd) ? ulong.Parse(sd) : 42;
                string policy = opt.TryGetValue("policy", out var p) ? p : "fixed";
                var r = RunOnce(level, build, policy, seed, duration);
                Console.WriteLine(JsonSerializer.Serialize(r, Json));
                return 0;
            }
            case "hash":
            {
                ulong seed = opt.TryGetValue("seed", out var sd) ? ulong.Parse(sd) : 42;
                int steps = opt.TryGetValue("steps", out var st) ? int.Parse(st) : 20000;
                var sim = new Simulation(level, seed, build);
                Attach(sim, "fixed");
                for (int i = 0; i < steps; i++) sim.Step();
                Console.WriteLine($"{sim.StateHash():x16}");
                return 0;
            }
            case "export":   // write the built-in levels as editable JSON
            {
                Directory.CreateDirectory("levels");
                foreach (var (name, lv) in Builtins())
                    File.WriteAllText($"levels/{name}.json", JsonSerializer.Serialize(lv, Json));
                Console.WriteLine("wrote levels/*.json");
                return 0;
            }
            default: Console.WriteLine($"unknown command {args[0]}"); return 1;
        }
    }

    // -------------------------------------------------------------------------

    record Result(string policy, float avgWaitSec, float throughputPerMin, float maxWaitSec, int spillbacks, int completed);

    static int Bench(LevelDef level, BuildVariant build, int seeds, float duration)
    {
        string[] policies = HasSignals(level, build)
            ? new[] { "fixed", "greedy", "maxpressure" }
            : new[] { "none" };

        Console.WriteLine($"level '{level.name}'  duration {duration}s  seeds {seeds}" +
                          (build != null ? "  [build variant applied]" : ""));
        Console.WriteLine($"{"policy",-12} {"avgWait",9} {"maxWait",9} {"thru/min",9} {"spill",6}");
        foreach (var p in policies)
        {
            float wait = 0, thru = 0, maxW = 0; int spill = 0, done = 0;
            for (ulong sd = 1; sd <= (ulong)seeds; sd++)
            {
                var r = RunOnce(level, build, p, sd * 1000, duration);
                wait += r.avgWaitSec; thru += r.throughputPerMin;
                maxW = Math.Max(maxW, r.maxWaitSec); spill += r.spillbacks; done += r.completed;
            }
            Console.WriteLine($"{p,-12} {wait / seeds,8:F2}s {maxW,8:F1}s {thru / seeds,9:F1} {spill,6}");
        }
        return 0;
    }

    static Result RunOnce(LevelDef level, BuildVariant build, string policy, ulong seed, float duration)
    {
        var sim = new Simulation(level, seed, build);
        Attach(sim, policy);
        int steps = (int)(duration / SimConfig.DT);
        for (int i = 0; i < steps; i++) sim.Step();
        return new Result(policy, sim.Metrics.LiveAvgWait(sim), sim.Metrics.ThroughputPerMin(sim),
                          sim.Metrics.MaxWait, sim.Metrics.SpillbackEvents, sim.Metrics.Completed);
    }

    static void Attach(Simulation sim, string policy)
    {
        foreach (var node in sim.Network.Nodes)
        {
            if (node.Control is SignalController ctl)
            {
                switch (policy)
                {
                    case "fixed": ctl.Policy = new FixedTimePolicy(30f, new[] { 0.5f, 0.5f }); break;
                    case "greedy": ctl.Policy = new GreedyPolicy(); ctl.DecisionInterval = 2f; break;
                    case "maxpressure": ctl.Policy = new MaxPressurePolicy(); ctl.DecisionInterval = 5f; break;
                    case "none": break;
                    default: throw new ArgumentException($"unknown policy '{policy}'");
                }
            }
        }
    }

    static bool HasSignals(LevelDef level, BuildVariant build)
    {
        foreach (var n in level.network.nodes)
        {
            var c = n.control;
            if (build != null)
            {
                int i = build.nodeIds.IndexOf(n.id);
                if (i >= 0) c = build.controls[i];
            }
            if (c == ControlType.Signalized) return true;
        }
        return false;
    }

    static LevelDef LoadLevel(Dictionary<string, string> opt)
    {
        if (opt.TryGetValue("level", out var path))
            return JsonSerializer.Deserialize<LevelDef>(File.ReadAllText(path), Json);
        string name = opt.TryGetValue("builtin", out var b) ? b : "fourway-signal";
        foreach (var (n, lv) in Builtins()) if (n == name) return lv;
        throw new ArgumentException($"unknown builtin '{name}'");
    }

    static IEnumerable<(string, LevelDef)> Builtins()
    {
        yield return ("fourway-signal", new LevelDef
        {
            name = "fourway-signal",
            network = NetworkBuilder.FourWay(ControlType.Signalized),
            demand = NetworkBuilder.SymmetricDemand(24f), duration = 600
        });
        yield return ("fourway-stop", new LevelDef
        {
            name = "fourway-stop",
            network = NetworkBuilder.FourWay(ControlType.AllWayStop),
            demand = NetworkBuilder.SymmetricDemand(24f), duration = 600
        });
        yield return ("fourway-roundabout", new LevelDef
        {
            name = "fourway-roundabout",
            network = NetworkBuilder.Roundabout(),
            demand = NetworkBuilder.SymmetricDemand(24f), duration = 600
        });
        yield return ("arterial3", Arterial3());
    }

    static LevelDef Arterial3()
    {
        var def = NetworkBuilder.Arterial(3);
        var demand = new DemandDef();
        demand.flows.Add(new OdFlowDef { origin = 200, dest = 201, rate = RateCurve.Constant(18) });
        demand.flows.Add(new OdFlowDef { origin = 201, dest = 200, rate = RateCurve.Constant(12) });
        for (int i = 0; i < 3; i++)
        {
            demand.flows.Add(new OdFlowDef { origin = 300 + i, dest = 400 + ((i + 1) % 3), rate = RateCurve.Constant(5) });
            demand.flows.Add(new OdFlowDef { origin = 400 + i, dest = 300 + ((i + 2) % 3), rate = RateCurve.Constant(4) });
        }
        return new LevelDef { name = "arterial3", network = def, demand = demand, duration = 600 };
    }

    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].StartsWith("--")) d[args[i].Substring(2)] = args[i + 1];
        return d;
    }
}
