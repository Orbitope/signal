using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// NxN signalized grid with boundary nodes on every edge stub. This is the
    /// canonical "city" for scaling tests and MARL curricula.
    /// Interior node ids: r*n+c. Boundary ids: 1000+ (allocated sequentially).
    /// </summary>
    public static class GridBuilder
    {
        public static NetworkDef Grid(int n, float spacing = 200f)
        {
            var def = new NetworkDef();
            int linkId = 1, boundaryId = 1000;
            void Add(int from, int to, float len)
                => def.links.Add(new LinkDef { id = linkId++, from = from, to = to, length = len });

            for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                def.nodes.Add(new NodeDef { id = r * n + c, x = c * spacing, y = r * spacing,
                                            control = ControlType.Signalized });

            // Interior links, both directions.
            for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                int id = r * n + c;
                if (c < n - 1) { Add(id, id + 1, spacing); Add(id + 1, id, spacing); }
                if (r < n - 1) { Add(id, id + n, spacing); Add(id + n, id, spacing); }
            }

            // Boundary stubs on the perimeter.
            var entries = new List<int>();
            void Stub(int interior, float bx, float by)
            {
                int b = boundaryId++;
                def.nodes.Add(new NodeDef { id = b, x = bx, y = by, isBoundary = true });
                Add(b, interior, spacing); Add(interior, b, spacing);
                entries.Add(b);
            }
            for (int c = 0; c < n; c++)
            {
                Stub(c, c * spacing, -spacing);                                  // south edge
                Stub((n - 1) * n + c, c * spacing, n * spacing);                 // north edge
            }
            for (int r = 0; r < n; r++)
            {
                Stub(r * n, -spacing, r * spacing);                              // west edge
                Stub(r * n + n - 1, n * spacing, r * spacing);                   // east edge
            }

            // Phases: EW green, then NS green, derived from real geometry.
            var net = RoadNetwork.Build(def);
            for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                int id = r * n + c;
                var node = net.NodeById(id);
                var ew = new List<int>(); var ns = new List<int>();
                foreach (int inId in node.InLinks)
                {
                    var l = net.LinkById(inId);
                    var from = net.NodeById(l.From);
                    if (Math.Abs(from.Y - node.Y) > Math.Abs(from.X - node.X)) ns.Add(inId);
                    else ew.Add(inId);
                }
                PhaseDef Make(List<int> ins)
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
                    return p;
                }
                def.nodes.Find(x => x.id == id).phases = new List<PhaseDef> { Make(ew), Make(ns) };
            }
            return def;
        }

        /// <summary>Uniform random OD demand between perimeter boundary nodes,
        /// scaled so per-intersection load stays constant as the grid grows.</summary>
        public static DemandDef GridDemand(int n, float vehPerMinPerIntersection, ulong seed = 7)
        {
            var def = new DemandDef();
            var boundaries = new List<int>();
            int count = 4 * n;
            for (int i = 0; i < count; i++) boundaries.Add(1000 + i);

            float total = vehPerMinPerIntersection * n * n;
            var rng = new Rng(seed);
            int pairs = Math.Min(count * 3, 200);
            float per = total / pairs;
            for (int i = 0; i < pairs; i++)
            {
                int o = boundaries[rng.RangeInt(0, count)];
                int d = boundaries[rng.RangeInt(0, count)];
                if (o == d) { i--; continue; }
                def.flows.Add(new OdFlowDef { origin = o, dest = d, rate = RateCurve.Constant(per) });
            }
            return def;
        }
    }
}
