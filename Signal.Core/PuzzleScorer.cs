using System;
using System.Collections.Generic;

namespace Signal.Core
{
    public sealed class ObjectiveResult
    {
        public ObjectiveDef Def;
        public bool Pass;
        public float Worst;          // the value on the worst seed
        public int WorstSeed;
        public float[] PerSeed;
    }

    public sealed class PuzzleResult
    {
        public bool Solved;
        public int Cost;
        public int Stars;
        public bool OverBudget;
        public string Error;         // an op that could not be applied; nothing was run
        public List<ObjectiveResult> Objectives = new List<ObjectiveResult>();
        public float MeanAvgWait, MeanCompleted, MeanMaxWait; public int TotalSpillbacks;

        public string Summary()
        {
            if (Error != null) return Error;
            var parts = new List<string>();
            foreach (var o in Objectives)
                parts.Add($"{(o.Pass ? "ok " : "NO ")}{o.Def.Describe()}: worst {o.Def.Format(o.Worst)}");
            return $"{(Solved ? "SOLVED" : "not solved")} ${Cost}{(OverBudget ? " (over budget)" : "")} " +
                   $"{new string('*', Stars)}  " + string.Join("; ", parts);
        }
    }

    /// <summary>
    /// Scores an answer honestly: every objective must hold on every seed, and
    /// stars depend on money spent against par. Determinism (StateHash) means
    /// the same ops replay identically, so results are reproducible.
    /// </summary>
    public static class PuzzleScorer
    {
        /// <summary>Authoring hook: swap the AI that runs the lights (null = MaxPressure).</summary>
        public static Func<SignalController, ISignalPolicy> AiOverride;

        /// <summary>The sim seed for a puzzle seed index. The game replays seed 0
        /// on screen with exactly this, so what the player watches is what was scored.</summary>
        public static ulong SeedFor(PuzzleDef puzzle, int index) => (ulong)puzzle.seeds[index] * 7919UL + 17UL;

        public static PuzzleResult Evaluate(PuzzleDef puzzle, IList<EditOp> ops, Action<int, Simulation> onSeedDone = null)
        {
            var r = new PuzzleResult();
            LevelDef level;
            try
            {
                level = Edits.Apply(puzzle.level, ops);
                r.Cost = Solution.From(ops).Cost(puzzle);
            }
            catch (PuzzleException e) { r.Error = e.Message; return r; }
            catch (InvalidOperationException e) { r.Error = e.Message; return r; }
            r.OverBudget = r.Cost > puzzle.budget;

            int n = puzzle.seeds.Count;
            foreach (var o in puzzle.objectives)
                r.Objectives.Add(new ObjectiveResult { Def = o, PerSeed = new float[n], Pass = true });

            int steps = (int)Math.Round(level.duration / SimConfig.DT);
            for (int s = 0; s < n; s++)
            {
                var sim = new Simulation(level, SeedFor(puzzle, s));
                Edits.AttachPolicies(sim, ops, puzzle.aiDecisionInterval, AiOverride);
                for (int i = 0; i < steps; i++) sim.Step();
                onSeedDone?.Invoke(s, sim);

                r.MeanAvgWait += sim.Metrics.LiveAvgWait(sim) / n;
                r.MeanCompleted += sim.Metrics.Completed / (float)n;
                r.MeanMaxWait += sim.Metrics.MaxWait / n;
                r.TotalSpillbacks += sim.Metrics.SpillbackEvents;

                foreach (var o in r.Objectives)
                {
                    float v = o.Def.Measure(sim);
                    o.PerSeed[s] = v;
                    bool worse = s == 0 || (o.Def.HigherIsBetter ? v < o.Worst : v > o.Worst);
                    if (worse) { o.Worst = v; o.WorstSeed = s; }
                    if (!o.Def.Passes(v)) o.Pass = false;
                }
            }

            r.Solved = true;
            foreach (var o in r.Objectives) if (!o.Pass) r.Solved = false;
            r.Stars = !r.Solved ? 0 : r.Cost <= puzzle.par ? 3 : r.Cost <= puzzle.par * 1.5f + 5 ? 2 : 1;
            return r;
        }

        /// <summary>Every single-tool answer the toolbox allows on this level, for
        /// authoring: run them all and you know whether the intended trick is
        /// the real best answer, and by how much.</summary>
        public static List<(string label, List<EditOp> ops)> Candidates(PuzzleDef puzzle)
        {
            var list = new List<(string, List<EditOp>)>();
            var lv = puzzle.level;
            var initial = puzzle.initialOps;
            list.Add(("as given", new List<EditOp>(initial)));
            if (initial.Count > 0) list.Add(("initial ops removed", new List<EditOp>()));
            if (puzzle.answer != null) list.Add(("AUTHORED ANSWER", new List<EditOp>(puzzle.answer)));

            // Multi-junction levels: also try the same control on every junction at once.
            var junctions = lv.network.nodes.FindAll(n => !n.isBoundary && lv.network.links.FindAll(l => l.to == n.id).Count >= 3);
            if (junctions.Count > 1)
                foreach (var tool in puzzle.toolbox)
                {
                    if (tool.kind == EditKind.SetControl && (tool.control == ControlType.TwoWayStop || tool.control == ControlType.YieldEntry))
                        for (int axis = 0; axis < 2; axis++)
                        {
                            var sol = Solution.From(initial);
                            foreach (var nd in junctions) sol.Add(new EditOp { kind = tool.kind, node = nd.id, control = tool.control, majorAxis = axis });
                            list.Add(($"{tool.label} everywhere ({(axis == 0 ? "E-W" : "N-S")} priority)", sol.Ops));
                        }
                    else if (tool.kind == EditKind.SetControl || tool.kind == EditKind.Roundabout)
                    {
                        var sol = Solution.From(initial);
                        foreach (var nd in junctions) sol.Add(new EditOp { kind = tool.kind, node = nd.id, control = tool.control });
                        list.Add(($"{tool.label} everywhere", sol.Ops));
                    }
                }

            foreach (var tool in puzzle.toolbox)
            {
                switch (tool.kind)
                {
                    case EditKind.SetControl:
                    case EditKind.Roundabout:
                    case EditKind.Retime:
                        foreach (var nd in lv.network.nodes)
                        {
                            if (nd.isBoundary) continue;
                            if (lv.network.links.FindAll(l => l.to == nd.id).Count < 3) continue;   // forks, ring nodes
                            if (tool.kind == EditKind.SetControl && (tool.control == ControlType.TwoWayStop || tool.control == ControlType.YieldEntry))
                            {
                                for (int axis = 0; axis < 2; axis++)
                                    list.Add(($"{tool.label} @{nd.id} ({(axis == 0 ? "E-W" : "N-S")} priority)",
                                              With(initial, nd.id, new EditOp { kind = tool.kind, node = nd.id, control = tool.control, majorAxis = axis })));
                            }
                            else if (tool.kind == EditKind.Retime)
                            {
                                foreach (var (name, splits) in new[] { ("even", new List<float> { 0.5f, 0.5f }), ("70/30", new List<float> { 0.7f, 0.3f }), ("30/70", new List<float> { 0.3f, 0.7f }) })
                                    list.Add(($"{tool.label} {name} @{nd.id}",
                                              With(initial, nd.id, new EditOp { kind = tool.kind, node = nd.id, cycle = 60f, splits = splits })));
                            }
                            else
                                list.Add(($"{tool.label} @{nd.id}",
                                          With(initial, nd.id, new EditOp { kind = tool.kind, node = nd.id, control = tool.control })));
                        }
                        break;
                    case EditKind.AddBay:
                    case EditKind.OneWay:
                    case EditKind.TurnBan:
                        if (tool.kind == EditKind.OneWay || tool.kind == EditKind.TurnBan)
                        {
                            // Streets between junctions, as pairs (a one-way pair, both lefts banned).
                            var inner = lv.network.links.FindAll(l => junctions.Exists(n => n.id == l.from) && junctions.Exists(n => n.id == l.to));
                            for (int a = 0; a < inner.Count && inner.Count <= 12; a++)
                            for (int b = a + 1; b < inner.Count; b++)
                            {
                                var ops = new List<EditOp>(initial);
                                var o1 = new EditOp { kind = tool.kind, link = inner[a].id }; var o2 = new EditOp { kind = tool.kind, link = inner[b].id };
                                if (tool.kind == EditKind.TurnBan) { o1.turns = TurnMask.Through | TurnMask.Right; o2.turns = o1.turns; }
                                ops.Add(o1); ops.Add(o2);
                                list.Add(($"{tool.label} links {inner[a].id}+{inner[b].id}", ops));
                            }
                        }
                        if (tool.kind == EditKind.AddBay)
                        {
                            // Bays usually come in opposing pairs; test every pair too.
                            var ids = new List<int>();
                            foreach (var l in lv.network.links)
                            {
                                var to = lv.network.nodes.Find(n => n.id == l.to);
                                if (to != null && !to.isBoundary) ids.Add(l.id);
                            }
                            for (int a = 0; a < ids.Count; a++)
                            for (int b = a + 1; b < ids.Count; b++)
                            {
                                var ops = new List<EditOp>(initial);
                                ops.Add(new EditOp { kind = EditKind.AddBay, link = ids[a] });
                                ops.Add(new EditOp { kind = EditKind.AddBay, link = ids[b] });
                                list.Add(($"{tool.label} links {ids[a]}+{ids[b]}", ops));
                            }
                        }
                        foreach (var l in lv.network.links)
                        {
                            var to = lv.network.nodes.Find(n => n.id == l.to);
                            if (to == null || to.isBoundary) continue;
                            var op = new EditOp { kind = tool.kind, link = l.id };
                            if (tool.kind == EditKind.TurnBan) op.turns = TurnMask.Through | TurnMask.Right;
                            var ops = new List<EditOp>(initial); ops.Add(op);
                            list.Add(($"{tool.label} link {l.id}", ops));
                        }
                        break;
                }
            }
            return list;
        }

        static List<EditOp> With(List<EditOp> initial, int nodeId, EditOp op)
        {
            var s = Solution.From(initial);
            s.Add(op);
            return s.Ops;
        }
    }
}
