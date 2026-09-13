using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>The priced tools. One place, so the toolbox tables and the UI agree.</summary>
    public static class Tools
    {
        public static ToolDef Signal() => new ToolDef { kind = EditKind.SetControl, control = ControlType.Signalized, price = 40, label = "Traffic signal", blurb = "Lights, run by the AI. Handles heavy traffic; costs everyone a little wait when it's quiet." };
        public static ToolDef AllWayStop() => new ToolDef { kind = EditKind.SetControl, control = ControlType.AllWayStop, price = 10, label = "All-way stop", blurb = "Everyone stops, first come first served. Cheap and quick when traffic is light." };
        public static ToolDef TwoWayStop() => new ToolDef { kind = EditKind.SetControl, control = ControlType.TwoWayStop, price = 8, label = "Two-way stop", blurb = "One street keeps priority; the other stops and waits for a gap." };
        public static ToolDef Roundabout() => new ToolDef { kind = EditKind.Roundabout, price = 60, label = "Roundabout", blurb = "Entering cars yield to the circle. Superb when flows are balanced." };
        public static ToolDef TurnBay() => new ToolDef { kind = EditKind.AddBay, price = 25, label = "Left-turn bay", blurb = "A separate lane for left turns, with its own protected green." };
        public static ToolDef NoLeft() => new ToolDef { kind = EditKind.TurnBan, price = 5, label = "No left turn", blurb = "Bans lefts from this lane. Cars find another way." };
        public static ToolDef OneWay() => new ToolDef { kind = EditKind.OneWay, price = 15, label = "One-way", blurb = "Removes this direction of the street." };
        public static ToolDef TimedPlan() => new ToolDef { kind = EditKind.Retime, price = 10, label = "Timed plan", blurb = "Fixed cycle and green split instead of the AI." };
    }

    /// <summary>
    /// The puzzle worlds. World 1 is authored here, in code, the way the
    /// scenario levels are. Every objective threshold was set from a measured
    /// run (signal-headless puzzle prints the table), not guessed.
    /// </summary>
    public static class Worlds
    {
        public static IReadOnlyList<WorldDef> All { get; } = new List<WorldDef> { World1() };

        public static PuzzleDef Find(string id)
        {
            foreach (var w in All) foreach (var p in w.puzzles) if (p.id == id) return p;
            return null;
        }

        // ------------------------------------------------------------ helpers

        const int Center = NetworkBuilder.Center;
        const int N = NetworkBuilder.N, E = NetworkBuilder.E, S = NetworkBuilder.S, W = NetworkBuilder.W;
        const int InN = 1, InE = 2, InS = 3, InW = 4;

        static LevelDef FourWay(string name, ControlType control, DemandDef demand, float duration = 300f)
            => new LevelDef { name = name, network = NetworkBuilder.FourWay(control), demand = demand, duration = duration };

        static OdFlowDef Flow(int o, int d, float vehPerMin)
            => new OdFlowDef { origin = o, dest = d, rate = RateCurve.Constant(vehPerMin) };

        static OdFlowDef Flow(int o, int d, RateCurve curve)
            => new OdFlowDef { origin = o, dest = d, rate = curve };

        static ObjectiveDef Avg(float s) => new ObjectiveDef { kind = ObjectiveKind.AvgWait, value = s };
        static ObjectiveDef Max(float s) => new ObjectiveDef { kind = ObjectiveKind.MaxWait, value = s };
        static ObjectiveDef Clear(int n) => new ObjectiveDef { kind = ObjectiveKind.ClearCars, value = n };

        /// <summary>Demand with a realistic turn mix: each arm sends its total
        /// mostly straight on, with a share turning right and a share turning left.
        /// (NetworkBuilder.SymmetricDemand sends a third of all cars left, which
        /// swamps a single-lane junction with permissive lefts long before the
        /// through traffic does.)</summary>
        public static DemandDef Mixed(float fromN, float fromE, float fromS, float fromW, float left = 0.15f, float right = 0.15f)
        {
            var d = new DemandDef();
            float thru = 1f - left - right;
            // (origin, straight-on, right, left) for right-hand traffic.
            AddArm(d, N, fromN, S, W, E, thru, right, left);
            AddArm(d, E, fromE, W, N, S, thru, right, left);
            AddArm(d, S, fromS, N, E, W, thru, right, left);
            AddArm(d, W, fromW, E, S, N, thru, right, left);
            return d;
        }

        static void AddArm(DemandDef d, int o, float total, int straight, int rightTo, int leftTo, float thru, float right, float left)
        {
            if (total <= 0f) return;
            if (thru > 0f) d.flows.Add(Flow(o, straight, total * thru));
            if (right > 0f) d.flows.Add(Flow(o, rightTo, total * right));
            if (left > 0f) d.flows.Add(Flow(o, leftTo, total * left));
        }

        public static DemandDef Balanced(float total, float left = 0.15f, float right = 0.15f)
            => Mixed(total / 4f, total / 4f, total / 4f, total / 4f, left, right);

        /// <summary>Balanced demand whose total follows a curve (rush hour).</summary>
        static DemandDef SymmetricCurve(float[] times, float[] totals, float left = 0.15f)
        {
            var d = new DemandDef();
            var shape = Balanced(1f, left);
            foreach (var f in shape.flows)
            {
                float share = f.rate.rates[0];
                var c = new RateCurve();
                for (int i = 0; i < times.Length; i++) { c.times.Add(times[i]); c.rates.Add(totals[i] * share); }
                d.flows.Add(Flow(f.origin, f.dest, c));
            }
            return d;
        }

        // ------------------------------------------------------------ World 1

        static WorldDef World1()
        {
            var w = new WorldDef
            {
                id = "w1", title = "One junction",
                blurb = "Eight puzzles on a single crossroads. Each one teaches a tool, and each answer is something the simulator measured, not a rule of thumb."
            };

            // 1. Light traffic: the stop sign beats the signal.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-1", title = "Quiet crossroads",
                intro = "A sleepy junction with a full traffic signal. Cars sit at red with nobody coming the other way. Find something cheaper that keeps them moving.",
                hint = "When traffic is light, stopping briefly beats waiting for a light to change.",
                level = FourWay("Quiet crossroads", ControlType.Signalized, Balanced(8f)),
                toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal() },
                budget = 20, par = 8,
                objectives = { Avg(7.5f) },
                answer = { new EditOp { kind = EditKind.SetControl, node = Center, control = ControlType.TwoWayStop, majorAxis = 1 } },
            });

            // 2. Heavy traffic: the stop sign collapses, the signal holds.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-2", title = "The busy hour",
                intro = "Same crossroads, four times the traffic, and an all-way stop that can't keep up. Queues stretch back out of sight.",
                hint = "A stop sign serves one car at a time. A signal serves a whole platoon.",
                level = FourWay("The busy hour", ControlType.AllWayStop, Balanced(30f, left: 0.05f)),
                toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal() },
                budget = 50, par = 40,
                objectives = { Avg(35f), Max(100f) },
                answer = { new EditOp { kind = EditKind.SetControl, node = Center, control = ControlType.Signalized } },
            });

            // 3. Balanced flows: the roundabout wins by a mile.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-3", title = "Round and round",
                intro = "Moderate, even traffic from all four sides, and a signal that makes half of them wait at any moment. There is a better shape for this.",
                hint = "When every direction is about equal, nobody has to stop for a light.",
                level = FourWay("Round and round", ControlType.Signalized, Balanced(22f)),
                toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout() },
                budget = 60, par = 60,
                objectives = { Avg(10.5f) },
                answer = { new EditOp { kind = EditKind.Roundabout, node = Center } },
            });

            // 4. The roundabout trap: one dominant flow monopolizes the circle.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-4", title = "One busy street",
                intro = "The roundabout from last time, but now a river of traffic pours north to south. Cars from the other arms can't find a gap in the circle, and some wait for minutes.",
                hint = "A roundabout is only fair when the flows are balanced. Something has to make the river stop now and then.",
                level = FourWay("One busy street", ControlType.AllWayStop, DominantDemand(16f, 8f)),
                initialOps = { new EditOp { kind = EditKind.Roundabout, node = Center } },
                toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout() },
                budget = 60, par = 40,
                objectives = { Avg(30f), Max(110f) },
                answer = { new EditOp { kind = EditKind.SetControl, node = Center, control = ControlType.Signalized } },
            });

            // 5. Side street: give the arterial priority.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-5", title = "The side street",
                intro = "A busy east-west road crossed by a quiet lane. The all-way stop makes the main road stop for nobody, over and over.",
                hint = "Only one street needs to stop.",
                level = FourWay("The side street", ControlType.AllWayStop, Mixed(1.5f, 7f, 1.5f, 7f)),
                toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal() },
                budget = 40, par = 8,
                objectives = { Avg(6f), Max(60f) },
                answer = { new EditOp { kind = EditKind.SetControl, node = Center, control = ControlType.TwoWayStop, majorAxis = 0 } },
            });

            // 6. Left turns block the through lane; a bay + protected phase fixes it.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-6", title = "Left behind",
                intro = "An old light on a fixed cycle. Lots of drivers turn left here, and each one waits in the through lane for a gap that never comes, holding up everyone behind them.",
                hint = "Give the left-turners their own lane and their own green. Which two approaches have the left-turners?",
                level = FourWay("Left behind", ControlType.Signalized, LeftHeavyDemand(0.715f)),
                initialOps = { new EditOp { kind = EditKind.Retime, node = Center, cycle = 80f } },
                toolbox = { Tools.TurnBay(), Tools.TimedPlan(), Tools.AllWayStop(), Tools.TwoWayStop() },
                budget = 60, par = 50,
                objectives = { Avg(36f) },
                answer = { new EditOp { kind = EditKind.Retime, node = Center, cycle = 80f }, new EditOp { kind = EditKind.AddBay, link = InN }, new EditOp { kind = EditKind.AddBay, link = InS } },
            });

            // 7. Fairness: the timed plan that starves the side street.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-7", title = "Fair play",
                intro = "Someone set this light to favour the main road: 85% of every cycle. The average wait looks fine. The side street is another story.",
                hint = "Remove the timed plan and the AI runs the light. Or find a split that treats both streets fairly.",
                level = FourWay("Fair play", ControlType.Signalized, Mixed(3f, 11f, 3f, 11f, left: 0f, right: 0.2f)),
                initialOps = { new EditOp { kind = EditKind.Retime, node = Center, cycle = 60f, splits = new List<float> { 0.15f, 0.85f } } },
                toolbox = { Tools.TimedPlan(), Tools.Signal() },
                budget = 20, par = 0,
                objectives = { Avg(18f), Max(60f) },
            });

            // 8. Rush hour: the off-peak fix fails at the peak.
            w.puzzles.Add(new PuzzleDef
            {
                id = "w1-8", title = "Rush hour",
                intro = "Quiet for the first few minutes, then the evening rush hits and the stop sign drowns. Whatever you build has to survive the peak.",
                hint = "Judge the junction by its worst ten minutes, not its best.",
                level = FourWay("Rush hour", ControlType.AllWayStop,
                                SymmetricCurve(new[] { 0f, 150f, 250f, 400f, 500f, 600f }, new[] { 6f, 6f, 30f, 30f, 6f, 6f }, left: 0.05f), 600f),
                toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout() },
                budget = 60, par = 40,
                objectives = { Avg(45f), Max(120f) },
                answer = { new EditOp { kind = EditKind.SetControl, node = Center, control = ControlType.Signalized } },
            });

            foreach (var p in w.puzzles) p.level.name = p.title;
            return w;
        }

        /// <summary>A river north to south on top of a light balanced background.</summary>
        public static DemandDef DominantDemand(float dominant, float backgroundTotal)
        {
            var d = Balanced(backgroundTotal);
            d.flows.Add(Flow(N, S, dominant));
            return d;
        }

        /// <summary>North-south traffic with a heavy share of left turns
        /// (N→E and S→W are lefts for right-hand traffic), light cross traffic.</summary>
        public static DemandDef LeftHeavyDemand(float k)
        {
            var d = new DemandDef();
            d.flows.Add(Flow(N, S, 9f * k)); d.flows.Add(Flow(S, N, 9f * k));
            d.flows.Add(Flow(N, E, 7f * k)); d.flows.Add(Flow(S, W, 7f * k));
            d.flows.Add(Flow(N, W, 1f * k)); d.flows.Add(Flow(S, E, 1f * k));
            d.flows.Add(Flow(E, W, 3f * k)); d.flows.Add(Flow(W, E, 3f * k));
            d.flows.Add(Flow(E, N, 1f * k)); d.flows.Add(Flow(E, S, 1f * k));
            d.flows.Add(Flow(W, N, 1f * k)); d.flows.Add(Flow(W, S, 1f * k));
            return d;
        }
    }
}
