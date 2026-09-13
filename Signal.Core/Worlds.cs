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
        public static IReadOnlyList<WorldDef> All { get; } = new List<WorldDef> { World1(), World2() };

        public static PuzzleDef Find(string id)
        {
            foreach (var w in All) foreach (var p in w.puzzles) if (p.id == id) return p;
            return null;
        }

        public static WorldDef WorldOf(PuzzleDef p)
        {
            foreach (var w in All) if (w.puzzles.Contains(p)) return w;
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
                tutorial = "How to play: click the junction on the map, pick a tool from the list, then press Run. The AI drives the lights; you decide what gets built. Every goal has to hold on three separate runs.",
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

        // ------------------------------------------------------------ World 2
        // Authored as editor documents: the same grid model the in-game editor
        // produces, so every one of these could have been built by a player.

        static WorldDef World2()
        {
            var w = new WorldDef
            {
                id = "w2", title = "Two lights", unlockStars = 9,
                blurb = "Junctions that affect each other. A queue at one light backs into the next, and the AI has to share the road."
            };

            // 1. Two junctions on a busy road with all-way stops.
            {
                var doc = Pair(spacing: 200f, preset: "east-west", total: 18f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-1", title = "Two in a row",
                    intro = "A busy road crosses two quiet streets a block apart, and someone put an all-way stop at both. The main road stops twice for nobody.",
                    tutorial = "Two junctions now. Each one is clicked and changed on its own, and the price adds up.",
                    hint = "The main road should keep priority at both junctions.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal() },
                    budget = 80, par = 16,
                    objectives = { Avg(8f), Max(70f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 1), control = ControlType.TwoWayStop, majorAxis = 0 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.TwoWayStop, majorAxis = 0 } },
                });
            }

            // 2. The short block: two signals close together spill into each other.
            {
                var doc = Pair(spacing: 90f, preset: "east-west", total: 28f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-2", title = "The short block",
                    intro = "Two all-way stops only a few car-lengths apart. Every car that stops at the second one backs into the first, and the block locks solid.",
                    hint = "The main road must never have to stop between the two. Give it priority at both.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.TimedPlan() },
                    budget = 40, par = 16,
                    objectives = { new ObjectiveDef { kind = ObjectiveKind.NoSpillback }, Avg(45f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 1), control = ControlType.TwoWayStop, majorAxis = 0 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.TwoWayStop, majorAxis = 0 } },
                });
            }

            // 3. Overbuilt: four signals on a quiet block; stops with the right priority flow better.
            {
                var doc = Block(preset: "east-west", total: 20f);
                var p = new PuzzleDef
                {
                    id = "w2-3", title = "Overbuilt",
                    intro = "A quiet block with a full traffic signal on every corner. At this volume the lights make everyone wait for nobody. Find something cheaper and quicker.",
                    hint = "Which way does most of the traffic go? Let that street keep priority at every corner.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.TwoWayStop(), Tools.AllWayStop(), Tools.Signal() },
                    budget = 40, par = 32,
                    objectives = { Avg(12f) },
                };
                foreach (var j in doc.junctions)
                    p.answer.Add(new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(j.gx, j.gy), control = ControlType.TwoWayStop, majorAxis = 0 });
                w.puzzles.Add(p);
            }

            // 4. Starved side streets along an arterial on timed plans.
            {
                var doc = Row(3, spacing: 200f, preset: "east-west", total: 22f);
                var p = new PuzzleDef
                {
                    id = "w2-4", title = "Three timed lights",
                    intro = "Three lights on the main road, each on a fixed plan that gives the side streets 15% of the cycle. Fast for the main road, brutal for everyone else.",
                    hint = "The AI shares time by need. Remove the plans, or set fairer splits.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.TimedPlan(), Tools.Signal() },
                    budget = 30, par = 0,
                    objectives = { Avg(20f), Max(75f) },
                };
                for (int gx = 1; gx <= 3; gx++)
                    p.initialOps.Add(new EditOp { kind = EditKind.Retime, node = EditorDoc.JunctionId(gx, 1), cycle = 60f, splits = new List<float> { 0.15f, 0.85f } });
                w.puzzles.Add(p);
            }

            // 5. Rush hour on the pair.
            {
                var doc = Pair(spacing: 200f, preset: "balanced", total: 15f, rush: true, duration: 600f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-5", title = "Rush hour, twice",
                    intro = "Two all-way stops that cope until the evening peak, then drown together. Whatever you build has to hold for the worst ten minutes.",
                    hint = "Stops serve one car at a time; lights serve platoons. You may not need to fix both junctions the same way.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout() },
                    budget = 120, par = 80,
                    objectives = { Avg(30f), Max(90f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 1), control = ControlType.Signalized },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.Signalized } },
                });
            }

            // 6. Two roundabouts, balanced traffic.
            {
                var doc = Pair(spacing: 200f, preset: "balanced", total: 26f);
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-6", title = "Round and round, twice",
                    intro = "Two signals a block apart with even traffic from every side. You have the money for a roundabout at each. Is it worth it at both?",
                    hint = "Balanced flows love a roundabout. Try one first and watch what happens at the other junction.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout() },
                    budget = 120, par = 120,
                    objectives = { Avg(12f) },
                    answer = { new EditOp { kind = EditKind.Roundabout, node = EditorDoc.JunctionId(2, 1) },
                               new EditOp { kind = EditKind.Roundabout, node = EditorDoc.JunctionId(3, 1) } },
                });
            }

            foreach (var p in w.puzzles) p.level.name = p.title;
            return w;
        }

        /// <summary>Two junctions side by side on row 1, every outer arm open.</summary>
        public static EditorDoc Pair(float spacing, string preset, float total, bool rush = false, float duration = 300f)
        {
            var doc = new EditorDoc { name = "pair", spacing = spacing, duration = duration };
            doc.AddJunction(2, 1); doc.AddJunction(3, 1);
            doc.Connect(2, 1, 3, 1);
            OpenAllFreeArms(doc);
            doc.demand = new DemandSpec { preset = preset, total = total, rush = rush };
            return doc;
        }

        /// <summary>A 2x2 block: rows 1 and 2, columns 2 and 3.</summary>
        public static EditorDoc Block(string preset, float total)
        {
            var doc = EditorDoc.Starter();
            doc.demand = new DemandSpec { preset = preset, total = total };
            return doc;
        }

        /// <summary>n junctions in a row on row 1.</summary>
        public static EditorDoc Row(int n, float spacing, string preset, float total)
        {
            var doc = new EditorDoc { name = "row", spacing = spacing };
            for (int gx = 1; gx <= n; gx++) doc.AddJunction(gx, 1);
            for (int gx = 1; gx < n; gx++) doc.Connect(gx, 1, gx + 1, 1);
            OpenAllFreeArms(doc);
            doc.demand = new DemandSpec { preset = preset, total = total };
            return doc;
        }

        static void OpenAllFreeArms(EditorDoc doc)
        {
            foreach (var j in doc.junctions)
                for (int d = 0; d < 4; d++)
                    if (doc.NeighbourVia(j, d, out _) == null) j.SetOpen(d, true);
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
