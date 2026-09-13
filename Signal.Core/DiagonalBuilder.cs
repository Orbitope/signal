using System;
using System.Collections.Generic;
using static Signal.Core.DowntownBuilder;

namespace Signal.Core
{
    /// <summary>
    /// Scenario 10: a one-way diagonal arterial cutting across a rectangular
    /// one-way-ish grid. Where the diagonal passes through a grid node it adds two
    /// extra legs (an inbound SW and an outbound NE), making 5–6-leg intersections
    /// with acute-angle turns — the case that forced the observation schema to grow
    /// from 4 to 8 octant approach slots (schema v3). Only right turns are allowed
    /// OFF the diagonal (its links are Through|Right: stay on the diagonal or peel
    /// right, never cross left).
    ///
    /// Phase plan is hybrid: ordinary 4-leg nodes keep the efficient EW/NS 2-phase;
    /// the diagonal nodes get a per-approach plan (one protected phase per incoming
    /// leg) since no simple 2-phase split is conflict-free at an acute crossing.
    /// </summary>
    public static class DiagonalBuilder
    {
        public static LevelDef Diagonal(int cols = 6, int rows = 4, float spacing = 200f)
        {
            // Base grid: two-way side streets, one two-way arterial across the middle.
            var h = new StreetSpec[rows]; var v = new StreetSpec[cols];
            for (int r = 0; r < rows; r++) h[r] = new StreetSpec(Flow.TwoWay, TurnMask.All);
            for (int c = 0; c < cols; c++) v[c] = new StreetSpec(Flow.TwoWay, TurnMask.All);
            var net = Build(cols, rows, h, v, null, spacing);

            int Id(int r, int c) => r * cols + c;
            int linkId = 1; foreach (var l in net.links) linkId = Math.Max(linkId, l.id + 1);
            var noLeft = TurnMask.Through | TurnMask.Right;
            void Add(int from, int to, float len) =>
                net.links.Add(new LinkDef { id = linkId++, from = from, to = to,
                                            length = len, speedLimit = FAST, turns = noLeft });

            int k = Math.Min(cols, rows);
            float diag = spacing * 1.4142f;
            var diagNodes = new HashSet<int>();
            // SW entry + NE exit boundaries for the diagonal.
            net.nodes.Add(new NodeDef { id = 4000, x = -spacing, y = -spacing, isBoundary = true });
            net.nodes.Add(new NodeDef { id = 4001, x = k * spacing, y = k * spacing, isBoundary = true });
            Add(4000, Id(0, 0), diag);
            for (int i = 0; i < k - 1; i++) { Add(Id(i, i), Id(i + 1, i + 1), diag); diagNodes.Add(Id(i, i)); }
            diagNodes.Add(Id(k - 1, k - 1));
            Add(Id(k - 1, k - 1), 4001, diag);

            DerivePhasesHybrid(net, diagNodes);

            // Demand: a heavy diagonal thoroughfare + grid background.
            var d = new DemandDef();
            d.flows.Add(F(4000, 4001, 26f));                    // the diagonal arterial
            EntriesExits(cols, rows, h, v, out var en, out var ex);
            en.Add(4000); ex.Add(4001);
            AddBackground(d, en, ex, 2.2f, 26, seed: 1010);
            return new LevelDef { name = "sc-diagonal", network = net, demand = d };
        }

        /// <summary>EW/NS 2-phase for ordinary nodes; one protected phase per
        /// incoming leg for the irregular diagonal nodes (always conflict-free
        /// because a single approach's movements only diverge).</summary>
        static void DerivePhasesHybrid(NetworkDef def, HashSet<int> diagNodes)
        {
            var net = RoadNetwork.Build(def);
            foreach (var nd in def.nodes)
            {
                if (nd.control != ControlType.Signalized) continue;
                var node = net.NodeById(nd.id);

                if (!diagNodes.Contains(nd.id))
                {
                    // cardinal 2-phase (reuse the downtown rule)
                    var ew = new List<int>(); var ns = new List<int>();
                    foreach (int inId in node.InLinks)
                    {
                        var from = net.NodeById(net.LinkById(inId).From);
                        if (Math.Abs(from.Y - node.Y) > Math.Abs(from.X - node.X)) ns.Add(inId);
                        else ew.Add(inId);
                    }
                    var phases = new List<PhaseDef>();
                    foreach (var ins in new[] { ew, ns })
                    {
                        var p = new PhaseDef();
                        foreach (var m in node.Movements)
                            if (ins.Contains(m.InLink))
                            {
                                p.movements.Add(m.Index);
                                foreach (var o in node.Movements)
                                    if (ins.Contains(o.InLink) && o.Index != m.Index &&
                                        node.Conflicts[m.Index, o.Index] &&
                                        NetworkBuilder.IsLeftTurn(net, node, m))
                                    { p.permissive.Add(m.Index); break; }
                            }
                        if (p.movements.Count > 0) phases.Add(p);
                    }
                    nd.phases = phases;
                }
                else
                {
                    // per-approach protected phases: one phase per in-link
                    var phases = new List<PhaseDef>();
                    foreach (int inId in node.InLinks)
                    {
                        var p = new PhaseDef();
                        foreach (var m in node.Movements)
                            if (m.InLink == inId) p.movements.Add(m.Index);
                        if (p.movements.Count > 0) phases.Add(p);
                    }
                    nd.phases = phases;
                }
            }
        }
    }
}
