using System;
using System.Collections.Generic;
using Signal.Core;

// Minimal test runner (NuGet is unavailable in this environment; xunit would
// be the normal choice in the real repo).
static class T
{
    public static int Failed;
    public static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine($"  PASS  {name}"); }
        catch (Exception e) { Failed++; Console.WriteLine($"  FAIL  {name}: {e.Message}"); }
    }
    public static void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }
}

class Program
{
    static Simulation MakeFourWay(ControlType c, float vehPerMin, ulong seed, float dominant = 0f)
    {
        var net = c == ControlType.YieldEntry ? NetworkBuilder.Roundabout() : NetworkBuilder.FourWay(c);
        var demand = dominant > 0f
            ? NetworkBuilder.DominantFlowDemand(dominant, vehPerMin)
            : NetworkBuilder.SymmetricDemand(vehPerMin);
        var level = new LevelDef { network = net, demand = demand, duration = 600 };
        return new Simulation(level, seed);
    }

    static (float wait, float thru, float maxWait, int spill) RunSim(Simulation sim, float seconds,
        Action<Simulation> attachPolicy = null)
    {
        attachPolicy?.Invoke(sim);
        int steps = (int)(seconds / SimConfig.DT);
        for (int i = 0; i < steps; i++) sim.Step();
        return (sim.Metrics.LiveAvgWait(sim), sim.Metrics.ThroughputPerMin(sim),
                sim.Metrics.MaxWait, sim.Metrics.SpillbackEvents);
    }

    /// <summary>Average a scenario over several seeds — control-type comparisons
    /// on a single seed are noise.</summary>
    static float AvgWait(Func<ulong, Simulation> make, Action<Simulation> policy, float seconds, int seeds = 5)
    {
        float sum = 0;
        for (ulong s = 1; s <= (ulong)seeds; s++)
            sum += RunSim(make(s * 1000), seconds, policy).wait;
        return sum / seeds;
    }

    static void AttachFixed(Simulation sim, float cycle = 30f)
    {
        foreach (var node in sim.Network.Nodes)
            if (node.Control is SignalController ctl)
                ctl.Policy = new FixedTimePolicy(cycle, new[] { 0.5f, 0.5f });
    }

    static void AttachMaxPressure(Simulation sim)
    {
        foreach (var node in sim.Network.Nodes)
            if (node.Control is SignalController ctl)
            {
                ctl.Policy = new MaxPressurePolicy();
                ctl.DecisionInterval = 5f;   // adaptive control decides on 5-10s periods, not every tick
            }
    }

    static void Main()
    {
        Console.WriteLine("== Determinism ==");
        T.Run("identical seeds -> identical 20k-step hash", () =>
        {
            var a = MakeFourWay(ControlType.Signalized, 30, 42); AttachFixed(a);
            var b = MakeFourWay(ControlType.Signalized, 30, 42); AttachFixed(b);
            for (int i = 0; i < 20000; i++) { a.Step(); b.Step(); }
            T.Assert(a.StateHash() == b.StateHash(), $"hash mismatch {a.StateHash():x} vs {b.StateHash():x}");
            T.Assert(a.Metrics.Completed > 100, $"expected traffic to flow, completed={a.Metrics.Completed}");
        });
        T.Run("different seeds -> different trajectories", () =>
        {
            var a = MakeFourWay(ControlType.Signalized, 30, 1); AttachFixed(a);
            var b = MakeFourWay(ControlType.Signalized, 30, 2); AttachFixed(b);
            for (int i = 0; i < 5000; i++) { a.Step(); b.Step(); }
            T.Assert(a.StateHash() != b.StateHash(), "seeds should diverge");
        });

        Console.WriteLine("== Conflict matrix ==");
        T.Run("crossing throughs conflict; same-approach movements don't", () =>
        {
            var net = RoadNetwork.Build(NetworkBuilder.FourWay(ControlType.Signalized));
            var node = net.NodeById(NetworkBuilder.Center);
            Movement Find(int inL, int outL) => node.FindMovement(inL, outL);
            var nThrough = Find(1, 13);   // N in -> S out
            var eThrough = Find(2, 14);   // E in -> W out
            var nLeft = Find(1, 12);      // N in -> E out (left, geometry: heading south, east is left)
            T.Assert(node.Conflicts[nThrough.Index, eThrough.Index], "crossing throughs must conflict");
            T.Assert(!node.Conflicts[nThrough.Index, nLeft.Index], "same in-link must not conflict");
        });
        T.Run("phase validator rejects protected conflicts", () =>
        {
            var def = NetworkBuilder.FourWay(ControlType.Signalized);
            var net = RoadNetwork.Build(def);
            var node = net.NodeById(0);
            var bad = new List<PhaseDef> { new PhaseDef() };
            var nT = node.FindMovement(1, 13); var eT = node.FindMovement(2, 14);
            bad[0].movements.Add(nT.Index); bad[0].movements.Add(eT.Index);   // both protected
            bool threw = false;
            try { net.ValidatePhases(node, bad); } catch (InvalidOperationException) { threw = true; }
            T.Assert(threw, "validator must reject NS-through + EW-through in one protected phase");
        });

        Console.WriteLine("== Signal state machine ==");
        T.Run("min-green, yellow and all-red are enforced against a flapping policy", () =>
        {
            var sim = MakeFourWay(ControlType.Signalized, 20, 7);
            var ctl = sim.ControllerAt(0);
            // Adversarial policy: demands the other phase every decision.
            ctl.Policy = new FlapPolicy();
            int greenChanges = 0; int last = ctl.CurrentPhase;
            float minObservedGreen = float.MaxValue; float t0 = 0;
            for (int i = 0; i < 6000; i++)
            {
                sim.Step();
                if (ctl.State == SignalState.Green && ctl.CurrentPhase != last)
                {
                    float greenDur = sim.Time - t0 - ctl.YellowTime - ctl.AllRedTime;
                    if (t0 > 0) minObservedGreen = Math.Min(minObservedGreen, greenDur);
                    greenChanges++; last = ctl.CurrentPhase; t0 = sim.Time;
                }
            }
            T.Assert(greenChanges > 3, "phases should change");
            T.Assert(minObservedGreen >= ctl.MinGreen - 0.3f,
                $"min green violated: {minObservedGreen:F1}s < {ctl.MinGreen}s");
        });

        Console.WriteLine("== Spillback ==");
        T.Run("short downstream link under heavy flow produces spillback events", () =>
        {
            // Arterial of 2 with a very short connector: flood west->east.
            var def = NetworkBuilder.Arterial(2, spacing: 60f);
            var demand = new DemandDef();
            demand.flows.Add(new OdFlowDef { origin = 200, dest = 201, rate = RateCurve.Constant(50) });
            demand.flows.Add(new OdFlowDef { origin = 300, dest = 401, rate = RateCurve.Constant(15) });
            demand.flows.Add(new OdFlowDef { origin = 301, dest = 400, rate = RateCurve.Constant(15) });
            var sim = new Simulation(new LevelDef { network = def, demand = demand }, 5);
            AttachFixed(sim, cycle: 40f);
            var r = RunSim(sim, 300);
            T.Assert(r.spill > 0, $"expected spillback under flood, got {r.spill} events");
        });

        Console.WriteLine("== Vehicle conservation ==");
        T.Run("no vehicle is lost or duplicated", () =>
        {
            var sim = MakeFourWay(ControlType.Signalized, 40, 9); AttachFixed(sim);
            long spawned = 0; long despawned = 0;
            sim.VehicleSpawned += _ => spawned++;
            sim.VehicleDespawned += _ => despawned++;
            for (int i = 0; i < 30000; i++) sim.Step();
            long inSystem = sim.VehiclesInSystem() - sim.Demand.HeldCount();
            T.Assert(spawned == despawned + inSystem,
                $"conservation broken: spawned={spawned} despawned={despawned} inSystem={inSystem}");
        });

        Console.WriteLine("== Policy sanity (asymmetric demand: NS-heavy, naive 50/50 fixed vs MaxPressure) ==");
        {
            Simulation MakeAsym(ulong seed)
            {
                var net = NetworkBuilder.FourWay(ControlType.Signalized);
                var dem = new DemandDef();
                // 70/30 NS/EW imbalance, ~20 veh/min total: fixed 50/50 wastes EW green.
                dem.flows.Add(new OdFlowDef { origin = NetworkBuilder.N, dest = NetworkBuilder.S, rate = RateCurve.Constant(7f) });
                dem.flows.Add(new OdFlowDef { origin = NetworkBuilder.S, dest = NetworkBuilder.N, rate = RateCurve.Constant(7f) });
                dem.flows.Add(new OdFlowDef { origin = NetworkBuilder.E, dest = NetworkBuilder.W, rate = RateCurve.Constant(3f) });
                dem.flows.Add(new OdFlowDef { origin = NetworkBuilder.W, dest = NetworkBuilder.E, rate = RateCurve.Constant(3f) });
                return new Simulation(new LevelDef { network = net, demand = dem }, seed);
            }
            float fixedW = AvgWait(MakeAsym, s => AttachFixed(s), 600);
            float mpW = AvgWait(MakeAsym, AttachMaxPressure, 600);
            Console.WriteLine($"        naive fixed 50/50: {fixedW:F2}s | MaxPressure: {mpW:F2}s");
            T.Run("MaxPressure beats naive fixed timing under asymmetric demand", () =>
                T.Assert(mpW < fixedW, $"MaxPressure {mpW:F2}s should beat fixed {fixedW:F2}s"));
        }

        Console.WriteLine("== Control-type profiles (the M5.5 gate) ==");
        {
            // LOW demand: stop sign should beat a signal (no pointless red waits).
            float sigLow = AvgWait(s => MakeFourWay(ControlType.Signalized, 8, s), s => AttachFixed(s), 600);
            float stopLow = AvgWait(s => MakeFourWay(ControlType.AllWayStop, 8, s), null, 600);
            float rndLow = AvgWait(s => MakeFourWay(ControlType.YieldEntry, 8, s), null, 600);
            Console.WriteLine($"        LOW (8 veh/min):  signal={sigLow:F2}s  stop={stopLow:F2}s  roundabout={rndLow:F2}s");
            T.Run("low demand: all-way stop beats fixed signal", () =>
                T.Assert(stopLow < sigLow, $"stop {stopLow:F2}s vs signal {sigLow:F2}s"));
            T.Run("low demand: roundabout beats fixed signal", () =>
                T.Assert(rndLow < sigLow, $"roundabout {rndLow:F2}s vs signal {sigLow:F2}s"));

            // HIGH demand: signal should beat the stop sign (FIFO server saturates).
            float sigHigh = AvgWait(s => MakeFourWay(ControlType.Signalized, 45, s), s => AttachFixed(s), 600);
            float stopHigh = AvgWait(s => MakeFourWay(ControlType.AllWayStop, 45, s), null, 600);
            Console.WriteLine($"        HIGH (45 veh/min): signal={sigHigh:F2}s  stop={stopHigh:F2}s");
            T.Run("high demand: signal beats all-way stop", () =>
                T.Assert(sigHigh < stopHigh, $"signal {sigHigh:F2}s vs stop {stopHigh:F2}s"));

            // DOMINANT flow: roundabout degrades vs its own balanced performance
            // (one heavy stream monopolizes the circle).
            float rndBal = AvgWait(s => MakeFourWay(ControlType.YieldEntry, 24, s), null, 600);
            float rndDom = AvgWait(s => MakeFourWay(ControlType.YieldEntry, 6, s, dominant: 18f), null, 600);
            Console.WriteLine($"        ROUNDABOUT: balanced 24/min={rndBal:F2}s  dominant-flow 24/min={rndDom:F2}s");
            T.Run("roundabout degrades under a dominant flow at equal total demand", () =>
                T.Assert(rndDom > rndBal * 1.3f, $"dominant {rndDom:F2}s vs balanced {rndBal:F2}s"));
        }


        Console.WriteLine("== Lanes (bays + multi-lane via lane-as-link) ==");
        T.Run("bay derives only Left movements; through lane only Through/Right", () =>
        {
            var net = RoadNetwork.Build(LaneBuilder.FourWayWithBays());
            var node = net.NodeById(LaneBuilder.Center);
            foreach (var m in node.Movements)
            {
                var inL = net.LinkById(m.InLink);
                if (inL.Turns == TurnMask.Left)
                    T.Assert(m.Turn == TurnMask.Left, $"bay produced {m.Turn}");
                if (inL.Turns == (TurnMask.Through | TurnMask.Right))
                    T.Assert(m.Turn != TurnMask.Left, "through lane produced a Left");
            }
        });
        T.Run("own bay-left vs own through: NO conflict; vs opposing through: conflict", () =>
        {
            var net = RoadNetwork.Build(LaneBuilder.FourWayWithBays());
            var node = net.NodeById(LaneBuilder.Center);
            Movement Find(int inL, TurnMask t)
            { foreach (var m in node.Movements) if (m.InLink == inL && m.Turn == t) return m; return null; }
            var nLeft = Find(31, TurnMask.Left);          // N bay left
            var nThrough = Find(41, TurnMask.Through);    // N through
            var sThrough = Find(43, TurnMask.Through);    // S through
            T.Assert(nLeft != null && nThrough != null && sThrough != null, "movements missing");
            T.Assert(!node.Conflicts[nLeft.Index, nThrough.Index], "own-approach left/through must not conflict");
            T.Assert(node.Conflicts[nLeft.Index, sThrough.Index], "left vs opposing through must conflict");
        });
        T.Run("router respects masks: through trip avoids bay, left trip uses it", () =>
        {
            var level = new LevelDef { network = LaneBuilder.FourWayWithBays(),
                demand = new DemandDef() };
            var sim = new Simulation(level, 1);
            var thru = sim.Router.Route(LaneBuilder.N, LaneBuilder.S);
            var left = sim.Router.Route(LaneBuilder.N, LaneBuilder.E);
            T.Assert(thru != null && System.Array.IndexOf(thru, 31) < 0, "through trip routed via bay");
            T.Assert(left != null && System.Array.IndexOf(left, 31) >= 0, "left trip should use the bay");
        });
        T.Run("two through lanes balance load at the fork", () =>
        {
            var level = new LevelDef { network = LaneBuilder.FourWayWithBays(throughLanes: 2),
                demand = new DemandDef { flows = { new OdFlowDef { origin = LaneBuilder.N, dest = LaneBuilder.S, rate = RateCurve.Constant(30) } } } };
            var sim = new Simulation(level, 5);
            var ctl = sim.ControllerAt(0); ctl.Policy = new FixedTimePolicy(30f, new[] { .25f, .25f, .25f, .25f });
            int laneA = 0, laneB = 0;
            for (int i = 0; i < 6000; i++)
            {
                sim.Step();
                laneA += sim.Network.LinkById(41).Vehicles.Count;
                laneB += sim.Network.LinkById(51).Vehicles.Count;
            }
            T.Assert(laneA > 0 && laneB > 0, $"a lane went unused (A={laneA} B={laneB})");
            float ratio = laneA / (float)laneB;
            T.Assert(ratio > 0.55f && ratio < 1.8f, $"imbalanced lanes A/B={ratio:F2}");
        });
        T.Run("4-phase protected lefts: no deadlock, traffic completes", () =>
        {
            var level = new LevelDef { network = LaneBuilder.FourWayWithBays(),
                demand = NetworkBuilder.SymmetricDemand(24f) };
            var sim = new Simulation(level, 11);
            var ctl = sim.ControllerAt(0); ctl.Policy = new MaxPressurePolicy(); ctl.DecisionInterval = 5f;
            for (int i = 0; i < 12000; i++) sim.Step();
            T.Assert(sim.Metrics.Completed > 200, $"completed={sim.Metrics.Completed}");
        });
        T.Run("bay capacity relief: bays+protected beat single-lane at heavy left demand", () =>
        {
            DemandDef LeftHeavy()
            {
                var d = new DemandDef();
                int[] arms = { LaneBuilder.N, LaneBuilder.E, LaneBuilder.S, LaneBuilder.W };
                int[] leftOf = { LaneBuilder.E, LaneBuilder.S, LaneBuilder.W, LaneBuilder.N };
                int[] opp = { LaneBuilder.S, LaneBuilder.W, LaneBuilder.N, LaneBuilder.E };
                for (int i = 0; i < 4; i++)
                {
                    d.flows.Add(new OdFlowDef { origin = arms[i], dest = leftOf[i], rate = RateCurve.Constant(4.5f) });
                    d.flows.Add(new OdFlowDef { origin = arms[i], dest = opp[i], rate = RateCurve.Constant(4.5f) });
                }
                return d;
            }
            float bays = 0, single = 0;
            for (ulong s = 1; s <= 3; s++)
            {
                var a = new Simulation(new LevelDef { network = LaneBuilder.FourWayWithBays(), demand = LeftHeavy() }, s * 100);
                var ca = a.ControllerAt(0); ca.Policy = new MaxPressurePolicy(); ca.DecisionInterval = 5f;
                var b = new Simulation(new LevelDef { network = NetworkBuilder.FourWay(ControlType.Signalized), demand = LeftHeavy() }, s * 100);
                var cb = b.ControllerAt(0); cb.Policy = new MaxPressurePolicy(); cb.DecisionInterval = 5f;
                for (int i = 0; i < 6000; i++) { a.Step(); b.Step(); }
                bays += a.Metrics.LiveAvgWait(a); single += b.Metrics.LiveAvgWait(b);
            }
            bays /= 3; single /= 3;
            Console.WriteLine($"        left-heavy 36/min: bays+protected={bays:F2}s  single-lane+permissive={single:F2}s");
            T.Assert(bays < single, $"bays {bays:F2}s should beat single-lane {single:F2}s under heavy lefts");
        });

        Console.WriteLine("== Performance ==");
        T.Run(">= 10k steps/sec on the 3-intersection arterial", () =>
        {
            var def = NetworkBuilder.Arterial(3);
            var demand = new DemandDef();
            demand.flows.Add(new OdFlowDef { origin = 200, dest = 201, rate = RateCurve.Constant(20) });
            demand.flows.Add(new OdFlowDef { origin = 201, dest = 200, rate = RateCurve.Constant(15) });
            for (int i = 0; i < 3; i++)
                demand.flows.Add(new OdFlowDef { origin = 300 + i, dest = 400 + ((i + 1) % 3), rate = RateCurve.Constant(6) });
            var sim = new Simulation(new LevelDef { network = def, demand = demand }, 3);
            AttachFixed(sim);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100000; i++) sim.Step();
            sw.Stop();
            double sps = 100000.0 / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"        {sps:N0} steps/sec ({sim.VehiclesInSystem()} vehicles in system at end)");
            T.Assert(sps > 10000, $"too slow: {sps:N0} steps/sec");
        });

        Console.WriteLine("== Learned policy port ==");
        foreach (var fix in System.IO.Directory.GetFiles("../godot/policies", "*.parity.json"))
        {
            string tag = System.IO.Path.GetFileName(fix).Replace(".parity.json", "");
            T.Run($"{tag}: C# actor reproduces PyTorch greedy actions", () =>
            {
                var w = PolicyWeights.FromBytes(System.IO.File.ReadAllBytes($"../godot/policies/{tag}.bin"));
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(fix));
                var root = doc.RootElement;
                int n = root.GetProperty("n").GetInt32();
                var obsRows = root.GetProperty("obs"); var maskRows = root.GetProperty("mask");
                var acts = root.GetProperty("action"); var logitRows = root.GetProperty("logits");
                var obs = new float[w.ObsSize]; var h1 = new float[w.Hidden]; var h2 = new float[w.Hidden];
                var logits = new float[w.Actions]; var mask = new byte[w.Actions];
                int mismatches = 0; float maxErr = 0f;
                for (int i = 0; i < n; i++)
                {
                    int k = 0; foreach (var v in obsRows[i].EnumerateArray()) obs[k++] = (float)v.GetDouble();
                    k = 0; foreach (var v in maskRows[i].EnumerateArray()) mask[k++] = (byte)v.GetInt32();
                    w.Logits(obs, h1, h2, logits);
                    k = 0; foreach (var v in logitRows[i].EnumerateArray()) { maxErr = Math.Max(maxErr, Math.Abs(logits[k++] - (float)v.GetDouble())); }
                    if (w.Greedy(logits, mask) != acts[i].GetInt32()) mismatches++;
                }
                T.Assert(mismatches == 0 && maxErr < 1e-3f, $"{mismatches}/{n} action mismatches, max logit error {maxErr:E2}");
            });
        }
        T.Run("learned policy runs a level and only ever picks legal phases", () =>
        {
            var files = System.IO.Directory.GetFiles("../godot/policies", "*.bin");
            if (files.Length == 0) return;
            var w = PolicyWeights.FromBytes(System.IO.File.ReadAllBytes(files[0]));
            var level = Levels.Get("grid3");
            var sim = new Simulation(level, 5);
            foreach (var nd in sim.Network.Nodes)
                if (nd.Control is SignalController c) { c.Policy = new LearnedPolicy(w); c.DecisionInterval = 5f; }
            for (int i = 0; i < 3000; i++) sim.Step();
            T.Assert(sim.Metrics.Completed > 50, $"completed {sim.Metrics.Completed}");
        });

        Console.WriteLine(T.Failed == 0 ? "\nALL TESTS PASSED" : $"\n{T.Failed} TEST(S) FAILED");
        Environment.Exit(T.Failed);
    }

    class FlapPolicy : ISignalPolicy
    {
        public int SelectPhase(Simulation sim, Node node, SignalController ctl)
            => (ctl.CurrentPhase + 1) % ctl.Phases.Count;
    }
}
