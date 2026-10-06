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

        Console.WriteLine("== Editor document ==");
        T.Run("starter doc builds a legal 2x2 level that runs", () =>
        {
            var doc = EditorDoc.Starter();
            T.Assert(doc.Problems().Count == 0, string.Join("; ", doc.Problems()));
            var lv = doc.Build();
            int signals = lv.network.nodes.FindAll(n => n.control == ControlType.Signalized).Count;
            int bounds = lv.network.nodes.FindAll(n => n.isBoundary).Count;
            T.Assert(signals == 4 && bounds == 8, $"{signals} signals, {bounds} boundary nodes");
            var sim = new Simulation(lv, 3);
            Edits.AttachPolicies(sim, doc.ops);
            for (int i = 0; i < 3000; i++) sim.Step();
            T.Assert(sim.Metrics.Completed > 40, $"completed {sim.Metrics.Completed}");
        });
        T.Run("doc edits: one-way street, roundabout op, remove junction cleans up", () =>
        {
            var doc = EditorDoc.Starter();
            var s = doc.StreetBetween(2, 1, 3, 1); s.ba = false;
            doc.ops.Add(new EditOp { kind = EditKind.Roundabout, node = EditorDoc.JunctionId(2, 2) });
            var lv = doc.Build();
            T.Assert(lv.network.links.Find(l => l.id == EditorDoc.StreetLinkId(3, 1, 3)) == null, "west-bound link removed");
            T.Assert(lv.network.nodes.Find(n => n.id == EditorDoc.JunctionId(2, 2)) == null, "roundabout replaced the junction");
            doc.RemoveJunction(2, 2);
            T.Assert(doc.ops.Count == 0 && doc.streets.Count == 2, $"ops {doc.ops.Count}, streets {doc.streets.Count}");
            // With (2,2) gone the one-way street strands east-side traffic bound for (2,1): Build must refuse, not silently drop it.
            bool refused = false;
            try { doc.Build(); } catch (PuzzleException e) { refused = e.Message.Contains("no way through"); }
            T.Assert(refused, "unroutable flow refused after removal");
            s.ba = true;
            T.Assert(doc.Build() != null, "routable again once the street is two-way");
        });
        T.Run("doc and exported puzzle round-trip through JSON", () =>
        {
            var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            var doc = EditorDoc.Starter();
            doc.ops.Add(new EditOp { kind = EditKind.AddBay, link = EditorDoc.EntryLinkId(2, 1, 0) });
            var back = System.Text.Json.JsonSerializer.Deserialize<EditorDoc>(System.Text.Json.JsonSerializer.Serialize(doc, json), json);
            T.Assert(back.junctions.Count == 4 && back.ops.Count == 1 && back.ops[0].kind == EditKind.AddBay, "doc round-trip");
            var p = new PuzzleDef { id = "user-test", title = "t", level = doc.Build(), initialOps = doc.ops, budget = 50, par = 25,
                                    toolbox = { Tools.Signal(), Tools.NoLeft() }, objectives = { new ObjectiveDef { kind = ObjectiveKind.AvgWait, value = 30 } } };
            var pb = System.Text.Json.JsonSerializer.Deserialize<PuzzleDef>(System.Text.Json.JsonSerializer.Serialize(p, json), json);
            var r = PuzzleScorer.Evaluate(pb, pb.initialOps);
            T.Assert(r.Error == null && pb.toolbox.Count == 2 && pb.level.network.links.Count == p.level.network.links.Count, r.Error ?? "puzzle round-trip");
        });

        Console.WriteLine("== Emergency preemption ==");
        {
            // Four-way with no ordinary traffic. Phase 0 serves north-south,
            // phase 1 east-west; the emergency vehicle runs east to west.
            Simulation MakeEmpty() => new Simulation(new LevelDef
                { network = NetworkBuilder.FourWay(ControlType.Signalized), demand = new DemandDef() }, 1);

            T.Run("preemption lets an emergency vehicle through a signal held red", () =>
            {
                Vehicle Run(float preemptDistance, out Simulation sim, out bool left)
                {
                    sim = MakeEmpty();
                    var ctl = sim.ControllerAt(0);
                    ctl.PreemptDistance = preemptDistance;
                    ctl.Policy = new ExternalPolicy();           // holds phase 0: north-south green
                    var ev = sim.SpawnEmergency(NetworkBuilder.E, NetworkBuilder.W);
                    T.Assert(ev != null && ev.IsEmergency, "emergency vehicle not spawned");
                    bool gone = false;
                    sim.VehicleDespawned += v => { if (v == ev) gone = true; };
                    for (int i = 0; i < 900; i++) sim.Step();
                    left = gone;
                    return ev;
                }
                Run(250f, out var a, out bool leftA);
                T.Assert(leftA && a.EmergencyCount == 0, "with preemption the emergency vehicle should leave the network");

                var evB = Run(0f, out var b, out bool leftB);
                var inLink = b.Network.LinkById(2);
                T.Assert(!leftB && b.EmergencyCount == 1, "with preemption off the emergency vehicle should not get through");
                T.Assert(inLink.Front == evB && inLink.Length - evB.Pos < 5f && evB.Speed < 0.1f,
                    $"expected it waiting at the stop line, {inLink.Length - evB.Pos:F1}m away at {evB.Speed:F1} m/s");
                T.Assert(b.ControllerAt(0).CurrentPhase == 0 && !b.ControllerAt(0).Preempting, "signal should still be held on phase 0");
            });

            T.Run("preemption ends once the emergency vehicle has passed", () =>
            {
                var sim = MakeEmpty();
                var ctl = sim.ControllerAt(0);
                var ev = sim.SpawnEmergency(NetworkBuilder.E, NetworkBuilder.W);
                bool gone = false, preempted = false;
                sim.VehicleDespawned += v => { if (v == ev) gone = true; };
                for (int i = 0; i < 900 && !gone; i++) { sim.Step(); preempted |= ctl.Preempting; }
                T.Assert(gone, "emergency vehicle never left the network");
                T.Assert(preempted, "controller never preempted");
                T.Assert(sim.EmergencyCount == 0, $"emergency count {sim.EmergencyCount}");
                T.Assert(!ctl.Preempting, "controller still preempting");
                T.Assert(ctl.CurrentPhase == 1 && ctl.State == SignalState.Green, "expected the east-west phase left green");
                ctl.RequestPhase(0);
                for (int i = 0; i < 150; i++) sim.Step();
                T.Assert(ctl.CurrentPhase == 0 && ctl.State == SignalState.Green,
                    $"outside request not honored: phase {ctl.CurrentPhase} {ctl.State}");
            });

            T.Run("preemption overrules requests made while it is in force", () =>
            {
                var sim = MakeEmpty();
                var ctl = sim.ControllerAt(0);
                var ev = sim.SpawnEmergency(NetworkBuilder.E, NetworkBuilder.W);
                bool gone = false;
                sim.VehicleDespawned += v => { if (v == ev) gone = true; };
                int preemptTicks = 0, servedTicks = 0; bool serving = false;
                for (int i = 0; i < 900 && !gone; i++)
                {
                    ctl.RequestPhase(0);                         // the outside caller wants north-south
                    sim.Step();
                    // Preempting is refreshed in the controller's tick, so on the step the
                    // vehicle crosses the line it still reads true; only judge while it approaches.
                    if (!ctl.Preempting || ev.RouteIdx > 0) { serving = false; continue; }
                    preemptTicks++;
                    T.Assert(ctl.PreemptPhase(sim, sim.Network.NodeById(0)) == 1, "preempt phase should be east-west");
                    bool green = ctl.CurrentPhase == 1 && ctl.State == SignalState.Green;
                    T.Assert(green || !serving, $"lost the emergency vehicle's green at t={sim.Time:F1}");
                    if (green) { serving = true; servedTicks++; }
                }
                T.Assert(preemptTicks > 0 && servedTicks > 0, $"preempting {preemptTicks} ticks, serving {servedTicks}");
                T.Assert(gone, "emergency vehicle never left the network");
                T.Assert(ev.Wait < 1f, $"emergency vehicle was held up: waited {ev.Wait:F1}s");
            });

            T.Run("signals with no emergency vehicle nearby keep following their policy", () =>
            {
                Simulation Make()
                {
                    var sim = new Simulation(new LevelDef { network = NetworkBuilder.Arterial(3), demand = new DemandDef() }, 1);
                    sim.ControllerAt(2).Policy = new FlapPolicy();
                    return sim;
                }
                var a = Make(); var b = Make();                  // b is the same run with no emergency vehicle
                var first = a.ControllerAt(0); var last = a.ControllerAt(2); var twin = b.ControllerAt(2);
                // West end to the first signal's south stub: never on the last signal's incoming links.
                T.Assert(a.SpawnEmergency(200, 400) != null, "no route");
                int changes = 0, steps = 0; bool firstPreempted = false; int lastPhase = last.CurrentPhase;
                while (a.EmergencyCount > 0 && steps < 2000)
                {
                    a.Step(); b.Step(); steps++;
                    firstPreempted |= first.Preempting;
                    T.Assert(!last.Preempting, "last signal preempting with no emergency vehicle on its links");
                    T.Assert(last.CurrentPhase == twin.CurrentPhase && last.State == twin.State,
                        $"last signal diverged from its policy at t={a.Time:F1}");
                    if (last.CurrentPhase != lastPhase) { changes++; lastPhase = last.CurrentPhase; }
                }
                T.Assert(a.EmergencyCount == 0, "emergency vehicle never left");
                T.Assert(firstPreempted, "first signal never preempted");
                T.Assert(changes >= 2, $"last signal changed phase {changes} times while the emergency vehicle existed");
            });

            T.Run("preemption keeps min-green, yellow and all-red", () =>
            {
                var sim = MakeFourWay(ControlType.Signalized, 20, 7);
                var ctl = sim.ControllerAt(0);
                const float tol = 0.15f;
                var state = ctl.State; int phase = ctl.CurrentPhase; int ticksInState = 0;
                int changes = 0, preemptTicks = 0, spawned = 0;
                for (int i = 0; i < 6000; i++)
                {
                    if (i % 70 == 0)                             // every 7s, from alternating streets
                    {
                        bool ns = (spawned++ % 2) == 0;
                        sim.SpawnEmergency(ns ? NetworkBuilder.N : NetworkBuilder.E, ns ? NetworkBuilder.S : NetworkBuilder.W);
                    }
                    sim.Step();
                    if (ctl.Preempting) preemptTicks++;
                    if (ctl.State == state)
                    {
                        T.Assert(ctl.CurrentPhase == phase, $"phase changed without leaving {state} at t={sim.Time:F1}");
                        ticksInState++;
                        continue;
                    }
                    float held = ticksInState * SimConfig.DT;
                    if (state == SignalState.Green)
                    {
                        T.Assert(ctl.State == SignalState.Yellow, $"green went to {ctl.State} at t={sim.Time:F1}");
                        T.Assert(held >= ctl.MinGreen - tol, $"min green violated: {held:F1}s at t={sim.Time:F1}");
                        T.Assert(ctl.CurrentPhase == phase, "phase changed on entering yellow");
                    }
                    else if (state == SignalState.Yellow)
                    {
                        T.Assert(ctl.State == SignalState.AllRed, $"yellow went to {ctl.State} at t={sim.Time:F1}");
                        T.Assert(held >= ctl.YellowTime - tol, $"yellow cut short: {held:F1}s at t={sim.Time:F1}");
                        T.Assert(ctl.CurrentPhase == phase, "phase changed on entering all-red");
                    }
                    else
                    {
                        T.Assert(ctl.State == SignalState.Green, $"all-red went to {ctl.State} at t={sim.Time:F1}");
                        T.Assert(held >= ctl.AllRedTime - tol, $"all-red cut short: {held:F1}s at t={sim.Time:F1}");
                        if (ctl.CurrentPhase != phase) changes++;
                    }
                    state = ctl.State; phase = ctl.CurrentPhase; ticksInState = 1;
                }
                T.Assert(preemptTicks > 0, "never preempted");
                T.Assert(changes > 10, $"only {changes} phase changes");
            });

            T.Run("an emergency vehicle enters ahead of cars held at a backed-up entrance", () =>
            {
                var demand = new DemandDef();
                demand.flows.Add(new OdFlowDef { origin = NetworkBuilder.N, dest = NetworkBuilder.S, rate = RateCurve.Constant(60) });
                var sim = new Simulation(new LevelDef { network = NetworkBuilder.FourWay(ControlType.Signalized), demand = demand }, 4);
                var ctl = sim.ControllerAt(0);
                ctl.RequestPhase(1);                             // hold the north approach red so it backs up
                var held = sim.Demand.EntryQueues[NetworkBuilder.N];
                for (int i = 0; i < 3000 && held.Count < 3; i++) sim.Step();
                T.Assert(held.Count >= 3, $"entrance never backed up ({held.Count} held)");

                int heldBefore = sim.Demand.HeldCount();
                var ev = sim.SpawnEmergency(NetworkBuilder.N, NetworkBuilder.S);
                T.Assert(ev != null, "no route");
                T.Assert(sim.Demand.HeldCount() == heldBefore + 1, "held count should include the emergency vehicle");
                var heldCars = new HashSet<Vehicle>(held);
                var entered = new List<Vehicle>();
                sim.VehicleSpawned += v => entered.Add(v);

                ctl.RequestPhase(0);                             // let the road clear so there is room to enter
                for (int i = 0; i < 1200 && entered.Count < 3; i++) sim.Step();
                T.Assert(entered.Count >= 3, $"only {entered.Count} vehicles entered");
                T.Assert(entered[0] == ev, "a held car entered before the emergency vehicle");
                T.Assert(heldCars.Contains(entered[1]), "the held cars should follow it in");
                T.Assert(ev.RouteIdx > 0 || sim.Network.LinkById(1).Vehicles.Contains(ev) || sim.EmergencyCount == 0,
                    "emergency vehicle should have entered the first link");
            });

            T.Run("spawning an emergency vehicle with no route returns null and changes nothing", () =>
            {
                // One one-way link from node 1 to node 2: nothing leads from 2 back to 1.
                var net = new NetworkDef();
                net.nodes.Add(new NodeDef { id = 1, x = 0, y = 0, isBoundary = true });
                net.nodes.Add(new NodeDef { id = 2, x = 100, y = 0, isBoundary = true });
                net.links.Add(new LinkDef { id = 1, from = 1, to = 2, length = 100f });
                var sim = new Simulation(new LevelDef { network = net, demand = new DemandDef() }, 1);
                T.Assert(sim.SpawnEmergency(2, 1) == null, "expected null with no route");
                T.Assert(sim.EmergencyCount == 0 && sim.Demand.HeldCount() == 0 && sim.Demand.EmergencyQueues.Count == 0,
                    "a refused spawn changed state");
                T.Assert(sim.SpawnEmergency(1, 2) != null, "the routable direction should spawn");
                T.Assert(sim.SpawnEmergency(2, 1) == null, "expected null with no route");
                T.Assert(sim.EmergencyCount == 1 && sim.Demand.HeldCount() == 1, $"count {sim.EmergencyCount}, held {sim.Demand.HeldCount()}");
            });

            T.Run("a run with no emergency vehicle has the same state hash as before the change", () =>
            {
                var sim = MakeFourWay(ControlType.Signalized, 20, 7);
                var ctl = sim.ControllerAt(0);
                for (int i = 0; i < 2000; i++) { sim.Step(); T.Assert(!ctl.Preempting, "preempting with no emergency vehicle"); }
                T.Assert(sim.EmergencyCount == 0, "emergency count should stay zero");
                T.Assert(sim.StateHash() == 1821680719510123714UL, $"state hash {sim.StateHash()} differs from main at 313ea68");
            });

            T.Run("an emergency vehicle in the network keeps at least 75% of the step rate", () =>
            {
                const int steps = 50000;
                double Rate(bool emergency)
                {
                    var demand = new DemandDef();
                    demand.flows.Add(new OdFlowDef { origin = 200, dest = 201, rate = RateCurve.Constant(20) });
                    demand.flows.Add(new OdFlowDef { origin = 201, dest = 200, rate = RateCurve.Constant(15) });
                    for (int i = 0; i < 3; i++)
                        demand.flows.Add(new OdFlowDef { origin = 300 + i, dest = 400 + ((i + 1) % 3), rate = RateCurve.Constant(6) });
                    var sim = new Simulation(new LevelDef { network = NetworkBuilder.Arterial(3), demand = demand }, 3);
                    AttachFixed(sim);
                    int present = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < steps; i++)
                    {
                        if (emergency && sim.EmergencyCount == 0) sim.SpawnEmergency(200, 201);
                        sim.Step();
                        if (sim.EmergencyCount > 0) present++;
                    }
                    sw.Stop();
                    // A vehicle that leaves is replaced before the next step, so at most
                    // the step it leaves on ends with none.
                    if (emergency) T.Assert(present > steps * 0.95, $"emergency vehicle present on only {present}/{steps} steps");
                    else T.Assert(present == 0, "baseline run had an emergency vehicle");
                    return steps / sw.Elapsed.TotalSeconds;
                }
                // Best of three, interleaved, so a scheduling hiccup doesn't decide the result.
                double without = 0, with = 0;
                for (int r = 0; r < 3; r++) { without = Math.Max(without, Rate(false)); with = Math.Max(with, Rate(true)); }
                Console.WriteLine($"        none: {without:N0} steps/sec | emergency always present: {with:N0} steps/sec ({with / without:P0})");
                T.Assert(with >= without * 0.75, $"{with:N0} steps/sec with an emergency vehicle vs {without:N0} without");
            });
        }

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
