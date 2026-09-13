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
        public static IReadOnlyList<WorldDef> All { get; } = new List<WorldDef> { World1(), World2(), World3() };

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
                intro = "A sleepy crossroads with a full traffic signal. Something cheaper would do, if you pick the right one.",
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
                intro = "Same crossroads, four times the traffic. The queues stretch back out of sight.",
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
                intro = "Moderate, even traffic from all four sides, and a signal in the middle of it.",
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
                intro = "The roundabout from last time, but the traffic has changed: a river pours north to south now. Watch the other three arms.",
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
                intro = "A busy east-west road crossed by a quiet lane, and an all-way stop.",
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
                intro = "An old light on a fixed cycle, and a lot of drivers who want to turn left. Watch what happens to the cars behind them.",
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
                intro = "Someone set this light to favour the main road: 85% of every cycle. The average wait looks fine.",
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
                intro = "Quiet for the first few minutes. Then the evening rush arrives. Whatever you build has to survive the peak.",
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
                id = "w2", title = "Networks", unlockStars = 9,
                blurb = "Junctions that affect each other. Nothing here has one obvious answer: read where the traffic goes, then spend where it matters."
            };

            // 1. The short block: two all-way stops close together spill into each other.
            {
                var doc = Pair(spacing: 90f, preset: "east-west", total: 28f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-1", title = "The short block",
                    intro = "Two junctions only a few car-lengths apart. Watch the block between them.",
                    hint = "Whatever stops at the second junction backs into the first. The main road must not have to stop between them.",
                    tutorial = "Two junctions now. Each one is clicked and changed on its own, and the prices add up.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.TimedPlan() },
                    budget = 40, par = 16,
                    objectives = { new ObjectiveDef { kind = ObjectiveKind.NoSpillback }, Avg(45f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 1), control = ControlType.TwoWayStop, majorAxis = 0 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.TwoWayStop, majorAxis = 0 } },
                });
            }

            // 2. The busy corner: only one junction needs the expensive fix, and the budget allows exactly one.
            {
                var doc = Block(preset: "balanced", total: 18f);
                doc.demand.Weigh(3, 1, 0, 7f).Weigh(3, 1, 1, 7f).Weigh(2, 2, 3, 0.3f).Weigh(2, 2, 2, 0.3f).Weigh(2, 1, 0, 0.3f).Weigh(3, 2, 2, 0.3f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-2", title = "The busy corner",
                    intro = "A block of four all-way stops. The traffic is not spread evenly, and neither is your money.",
                    hint = "Run it once and watch which corner the queues form at. That corner needs more than a stop sign; the rest may not need anything.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout() },
                    budget = 60, par = 40,
                    objectives = { Avg(12f), Max(70f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.Signalized } },
                });
            }

            // 3. Two busy corners: the cheapest fix is at the QUIET corners.
            {
                var doc = Block(preset: "balanced", total: 16f);
                doc.demand.Weigh(3, 1, 0, 5f).Weigh(3, 1, 1, 5f).Weigh(2, 2, 3, 5f).Weigh(2, 2, 2, 5f).Weigh(2, 1, 0, 0.3f).Weigh(2, 1, 3, 0.3f).Weigh(3, 2, 1, 0.3f).Weigh(3, 2, 2, 0.3f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-3", title = "Two busy corners",
                    intro = "Two corners of this block carry most of the traffic; the other two barely see a car. Every corner is an all-way stop. Twenty dollars will do it, if you spend them in the right place.",
                    hint = "The busy corners are not where the money goes. What is holding the cars up is the stops they hit on the way through the quiet corners.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal() },
                    budget = 40, par = 16,
                    objectives = { Avg(13f), Max(65f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 2), control = ControlType.TwoWayStop, majorAxis = 0 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.TwoWayStop, majorAxis = 0 } },
                });
            }

            // 4. Four in a row: one signal, placed right, and one junction left alone.
            {
                var doc = Row(4, spacing: 200f, preset: "east-west", total: 24f);
                doc.demand.Weigh(1, 1, 0, 3f).Weigh(1, 1, 2, 3f).Weigh(3, 1, 0, 3f).Weigh(3, 1, 2, 3f)
                          .Weigh(2, 1, 0, 0.25f).Weigh(2, 1, 2, 0.25f).Weigh(4, 1, 0, 0.25f).Weigh(4, 1, 2, 0.25f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-4", title = "Four in a row",
                    intro = "A long main road with four cross streets, all of them all-way stops. You can afford one signal. Where, and what happens to the other three, is the whole puzzle.",
                    hint = "Two of the cross streets are busy, but only the first one needs the signal. And not every corner is better off changed.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal() },
                    budget = 70, par = 56,
                    objectives = { Avg(14f), Max(75f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(1, 1), control = ControlType.Signalized },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 1), control = ControlType.TwoWayStop, majorAxis = 0 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.TwoWayStop, majorAxis = 0 } },
                });
            }

            // 5. Rush hour, twice.
            {
                var doc = Pair(spacing: 200f, preset: "balanced", total: 15f, rush: true, duration: 600f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-5", title = "Rush hour, twice",
                    intro = "Two all-way stops and an evening peak. Judge your answer by the worst ten minutes, not the first five.",
                    hint = "Stops serve one car at a time; lights serve platoons. You may not need to fix both junctions the same way, and one roundabout can be enough.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout() },
                    budget = 120, par = 60,
                    objectives = { Avg(30f), Max(90f) },
                    answer = { new EditOp { kind = EditKind.Roundabout, node = EditorDoc.JunctionId(3, 1) } },
                });
            }

            // 6. Three timed lights: fairness across a row.
            {
                var doc = Row(3, spacing: 200f, preset: "east-west", total: 22f);
                var p = new PuzzleDef
                {
                    id = "w2-6", title = "Three timed lights",
                    intro = "Three lights on the main road, each on a fixed plan someone tuned for the main road. The side streets have opinions.",
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

            // 7. Two roundabouts.
            {
                var doc = Pair(spacing: 200f, preset: "balanced", total: 26f);
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w2-7", title = "Round and round, twice",
                    intro = "Two signals a block apart with even traffic from every side, and money for two roundabouts. Is it worth it at both?",
                    hint = "Try one first and watch what happens at the other junction.",
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

        // ------------------------------------------------------------ World 3: real roads
        // Situations lifted from real streets. Calibrated with --search like World 2.

        static WorldDef World3()
        {
            var w = new WorldDef
            {
                id = "w3", title = "Real roads", unlockStars = 24,
                blurb = "Situations you have sat in. A diamond interchange, frontage roads, a stadium letting out. Bigger maps, more tools, and the queue that matters is not always the one you can see."
            };

            // 1. The diamond interchange: an arterial passes under a highway; two ramp
            //    terminals a block apart; cross streets at either end.
            {
                var doc = new EditorDoc { name = "interchange", spacing = 200f, duration = 600f, cols = 5, rows = 5 };
                for (int gy = 1; gy <= 4; gy++) doc.AddJunction(2, gy);
                for (int gy = 1; gy < 4; gy++) doc.Connect(2, gy, 2, gy + 1);
                var n = doc.JunctionAt(2, 1); n.openN = true; n.openE = true; n.openW = true;
                var t1 = doc.JunctionAt(2, 2); t1.openE = true; t1.modeE = 1; t1.openW = true; t1.modeW = 2;   // off-ramp from the east, on-ramp to the west
                var t2 = doc.JunctionAt(2, 3); t2.openW = true; t2.modeW = 1; t2.openE = true; t2.modeE = 2;   // off-ramp from the west, on-ramp to the east
                var sj = doc.JunctionAt(2, 4); sj.openS = true; sj.openE = true; sj.openW = true;
                foreach (var j in doc.junctions) { j.control = ControlType.TwoWayStop; j.majorAxis = 1; }   // the main road keeps priority everywhere
                doc.demand = new DemandSpec { preset = "north-south", total = 20f, rush = true };
                doc.demand.Weigh(2, 2, 1, 5f).Weigh(2, 3, 3, 5f).Weigh(2, 2, 3, 2.5f).Weigh(2, 3, 1, 2.5f)
                          .Weigh(2, 1, 1, 0.4f).Weigh(2, 1, 3, 0.4f).Weigh(2, 4, 1, 0.4f).Weigh(2, 4, 3, 0.4f);
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w3-1", title = "The interchange",
                    intro = "A main road passes under the highway. Two off-ramps dump commuters onto it a block apart, two on-ramps take them away, and the cross streets at each end want their turn. The main road keeps priority at every junction. When the evening peak hits, watch the ramps: a queue that backs up an off-ramp is standing on the highway.",
                    hint = "The off-ramp queue is the one that matters, and it is the one you can't see. At the ramp terminals the ramp has to be the street that keeps priority, or get its own green.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout(), Tools.TurnBay(), Tools.NoLeft(), Tools.TimedPlan() },
                    budget = 120, par = 16,
                    objectives = { new ObjectiveDef { kind = ObjectiveKind.GateQueue, value = 6 }, Avg(30f), Max(160f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 2), control = ControlType.TwoWayStop, majorAxis = 0 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 3), control = ControlType.TwoWayStop, majorAxis = 0 } },
                });
            }

            // 2. Frontage roads: one-way pair either side of the highway, cross streets
            //    bridging, ramps feeding each frontage road at its upstream end.
            {
                var doc = new EditorDoc { name = "frontage", spacing = 200f, duration = 600f, cols = 6, rows = 4 };
                for (int gx = 1; gx <= 4; gx++) { doc.AddJunction(gx, 1); doc.AddJunction(gx, 2); }
                for (int gx = 1; gx < 4; gx++) { doc.Connect(gx, 1, gx + 1, 1); doc.Connect(gx, 2, gx + 1, 2); }
                foreach (var st in doc.streets)
                {
                    if (st.ay == 1 && st.by == 1) st.ba = false;   // row 1 eastbound only
                    if (st.ay == 2 && st.by == 2) st.ab = false;   // row 2 westbound only
                }
                doc.Connect(1, 1, 1, 2); doc.Connect(4, 1, 4, 2);   // bridges at the ends
                doc.JunctionAt(1, 1).openW = true; doc.JunctionAt(1, 1).modeW = 1;   // ramp onto the eastbound frontage
                doc.JunctionAt(4, 1).openE = true; doc.JunctionAt(4, 1).modeE = 2;   // eastbound leaves
                doc.JunctionAt(4, 2).openE = true; doc.JunctionAt(4, 2).modeE = 1;   // ramp onto the westbound frontage
                doc.JunctionAt(1, 2).openW = true; doc.JunctionAt(1, 2).modeW = 2;   // westbound leaves
                for (int gx = 1; gx <= 4; gx++) { doc.JunctionAt(gx, 1).openN = true; doc.JunctionAt(gx, 2).openS = true; }
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                doc.demand = new DemandSpec { preset = "east-west", total = 27f };
                doc.demand.Weigh(1, 1, 3, 4f).Weigh(4, 2, 1, 4f).Weigh(4, 1, 1, 3f).Weigh(1, 2, 3, 3f)
                          .Weigh(3, 1, 0, 4f).Weigh(3, 2, 2, 4f).Weigh(2, 1, 0, 0.3f).Weigh(2, 2, 2, 0.3f);
                var p = new PuzzleDef
                {
                    id = "w3-2", title = "Frontage roads",
                    intro = "Two one-way frontage roads run either side of the highway, fed by ramps at their upstream ends, with local streets crossing between them. Eight all-way stops. Traffic mostly wants to go straight along the frontage roads; the locals want across.",
                    hint = "One-way roads have no opposing traffic, which makes one control type much better value than usual. Bridges at the ends carry the U-turn traffic.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.NoLeft() },
                    budget = 70, par = 56,
                    objectives = { Avg(19f), Max(155f) },
                };
                foreach (var j in doc.junctions)
                    if (!(j.gx == 4 && j.gy == 2))
                        p.answer.Add(new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(j.gx, j.gy), control = ControlType.TwoWayStop, majorAxis = 0 });
                w.puzzles.Add(p);
            }

            // 3. The stadium letting out: one entrance floods a quiet grid.
            {
                var doc = Row(3, spacing: 200f, preset: "balanced", total: 19f);
                doc.duration = 600f; doc.demand.rush = true;
                doc.JunctionAt(2, 1).modeS = 1;                                   // the car park exit: entry only
                doc.demand.Weigh(2, 1, 2, 12f).Weigh(1, 1, 3, 2f).Weigh(3, 1, 1, 2f).Weigh(2, 1, 0, 0.2f);
                foreach (var j in doc.junctions) j.control = ControlType.AllWayStop;
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w3-3", title = "Full time",
                    intro = "The match ends and the car park empties onto the middle junction of a quiet street. Most of them head for the ends of the road. Nothing else is going on, until it is.",
                    hint = "An all-way stop serves the car park one car at a time. Something has to let the platoon out, without stranding the road it lands on.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.Roundabout(), Tools.NoLeft() },
                    budget = 60, par = 24,
                    objectives = { new ObjectiveDef { kind = ObjectiveKind.GateQueue, value = 10 }, Avg(30f) },
                    answer = { new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(1, 1), control = ControlType.TwoWayStop, majorAxis = 0 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(2, 1), control = ControlType.TwoWayStop, majorAxis = 1 },
                               new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(3, 1), control = ControlType.TwoWayStop, majorAxis = 0 } },
                });
            }

            // 4. Right in, right out: side-street lefts across an arterial; ban them and the block carries the detour.
            {
                var doc = Block(preset: "balanced", total: 26f);
                doc.demand.Weigh(2, 1, 3, 4f).Weigh(3, 1, 1, 4f).Weigh(2, 2, 3, 0.5f).Weigh(3, 2, 1, 0.5f);
                foreach (var j in doc.junctions) { j.control = ControlType.TwoWayStop; j.majorAxis = 0; }
                w.puzzles.Add(new PuzzleDef
                {
                    id = "w3-4", title = "Right in, right out",
                    intro = "The top street is the arterial and keeps priority at both corners. Drivers coming off the side streets want to turn left across it, and each one sits at the stop line waiting for a gap that the arterial rarely offers. Everyone behind them waits too.",
                    hint = "You can't buy a gap. You can ban the turn: a driver who can't turn left goes right, round the block, and arrives from the other side. Cheap, if the block can take it.",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.NoLeft(), Tools.TwoWayStop(), Tools.AllWayStop(), Tools.Signal() },
                    budget = 40, par = 10,
                    objectives = { Avg(12f), Max(70f) },
                    answer = { new EditOp { kind = EditKind.TurnBan, link = EditorDoc.StreetLinkId(2, 2, 0), turns = TurnMask.Through | TurnMask.Right },
                               new EditOp { kind = EditKind.TurnBan, link = EditorDoc.StreetLinkId(3, 1, 3), turns = TurnMask.Through | TurnMask.Right } },
                });
            }

            // 5. The district: a big grid as context, four corners to fix.
            {
                var doc = GridDoc(8, 5, 160f, "east-west", 32f);
                foreach (var j in doc.junctions) { j.control = ControlType.TwoWayStop; j.majorAxis = 1; }   // side streets keep priority everywhere
                // The arterial is row 3: its ends carry the district's through traffic, and it
                // already has priority except at the four corners in the middle.
                foreach (var j in doc.junctions) if (j.gy == 3 && (j.gx <= 2 || j.gx >= 7)) j.majorAxis = 0;
                doc.demand.Weigh(1, 3, 3, 20f).Weigh(8, 3, 1, 20f);
                doc.demand.Weigh(5, 1, 0, 5f).Weigh(5, 5, 2, 5f);      // one busy cross street, through the middle
                for (int gx = 1; gx <= 8; gx++) if (gx != 5) { doc.demand.Weigh(gx, 1, 0, 0.4f); doc.demand.Weigh(gx, 5, 2, 0.4f); }
                var p = new PuzzleDef
                {
                    id = "w3-5", title = "The district",
                    intro = "Forty junctions, and a main road running east-west through the middle. Somewhere in the centre it loses its priority. You may change the four ringed junctions and nothing else, and you don't need all of them.",
                    hint = "The ringed corners sit on the main road. What does the main road need at a corner, and what do its side streets need?",
                    level = doc.BuildRaw(),
                    toolbox = { Tools.AllWayStop(), Tools.TwoWayStop(), Tools.Signal(), Tools.NoLeft() },
                    budget = 60, par = 24,
                    objectives = { Avg(20f), Max(145f) },
                };
                foreach (int gx in new[] { 3, 4, 5, 6 }) p.editable.Add(EditorDoc.JunctionId(gx, 3));
                foreach (int gx in new[] { 4, 5, 6 })
                    p.answer.Add(new EditOp { kind = EditKind.SetControl, node = EditorDoc.JunctionId(gx, 3), control = ControlType.TwoWayStop, majorAxis = 0 });
                w.puzzles.Add(p);
            }

            foreach (var p in w.puzzles) p.level.name = p.title;
            return w;
        }

        /// <summary>A full grid of junctions, every outer arm open.</summary>
        public static EditorDoc GridDoc(int cols, int rows, float spacing, string preset, float total)
        {
            var doc = new EditorDoc { name = "grid", spacing = spacing, cols = cols + 2, rows = rows + 2 };
            for (int gx = 1; gx <= cols; gx++) for (int gy = 1; gy <= rows; gy++) doc.AddJunction(gx, gy);
            for (int gx = 1; gx <= cols; gx++) for (int gy = 1; gy <= rows; gy++)
            {
                if (gx < cols) doc.Connect(gx, gy, gx + 1, gy);
                if (gy < rows) doc.Connect(gx, gy, gx, gy + 1);
            }
            OpenAllFreeArms(doc);
            doc.demand = new DemandSpec { preset = preset, total = total };
            return doc;
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
