using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// A "downtown corridor": a cols x rows signalized grid that is NOT uniform.
    /// Unlike GridBuilder (every street identical, demand spread evenly), this
    /// authors real road hierarchy and directionality — the setting where a
    /// green wave, and therefore coordination, actually pays:
    ///
    ///   * PRIMARY ARTERIAL   — the middle row, a fast two-way thoroughfare
    ///                          carrying heavy east-west through traffic.
    ///   * SECONDARY ARTERIAL — the middle column, a moderate two-way N-S road.
    ///   * ONE-WAY SIDE STREET— the bottom row, eastbound only (the reverse
    ///                          links simply don't exist; the router honors it).
    ///   * SIDE STREETS        — everything else, slower two-way, light traffic.
    ///
    /// Same 105-float obs / Discrete(8) schema as every other level: two phases
    /// (EW green, NS green) per intersection, derived from geometry and validated
    /// at load. Interior ids r*cols+c; boundaries 2000+r (W), 2100+r (E),
    /// 2200+c (S), 2300+c (N).
    /// </summary>
    public static class CorridorBuilder
    {
        public const float FAST = 16.7f;   // ~60 km/h arterial
        public const float SLOW = 11.1f;   // ~40 km/h side street

        public static int WBound(int r) => 2000 + r;
        public static int EBound(int r) => 2100 + r;
        public static int SBound(int c) => 2200 + c;
        public static int NBound(int c) => 2300 + c;
        public static int MidRow(int rows) => rows / 2;
        public static int MidCol(int cols) => cols / 2;
        public const int OneWayRow = 0;    // bottom row: eastbound one-way

        public static NetworkDef Corridor(int cols, int rows, float spacing = 200f)
        {
            if (rows < 2 || cols < 2) throw new ArgumentException("corridor needs >=2x2");
            var def = new NetworkDef();
            int linkId = 1;
            void Add(int from, int to, float speed)
                => def.links.Add(new LinkDef { id = linkId++, from = from, to = to,
                                               length = spacing, speedLimit = speed });

            int midRow = MidRow(rows), midCol = MidCol(cols);
            int Id(int r, int c) => r * cols + c;

            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                def.nodes.Add(new NodeDef { id = Id(r, c), x = c * spacing, y = r * spacing,
                                            control = ControlType.Signalized });

            // Horizontal streets. The middle row is the fast arterial; the bottom
            // row is one-way eastbound (no westbound link authored).
            for (int r = 0; r < rows; r++)
            {
                float sp = r == midRow ? FAST : SLOW;
                bool oneWay = r == OneWayRow;
                for (int c = 0; c < cols - 1; c++)
                {
                    Add(Id(r, c), Id(r, c + 1), sp);                 // eastbound
                    if (!oneWay) Add(Id(r, c + 1), Id(r, c), sp);    // westbound
                }
            }
            // Vertical streets. The middle column is the secondary arterial.
            for (int c = 0; c < cols; c++)
            {
                float sp = c == midCol ? FAST : SLOW;
                for (int r = 0; r < rows - 1; r++)
                {
                    Add(Id(r, c), Id(r + 1, c), sp);                 // northbound
                    Add(Id(r + 1, c), Id(r, c), sp);                 // southbound
                }
            }

            // Boundary stubs. Arterial ends inherit the arterial speed; the one-way
            // row gets an entry stub in the west and a sink stub in the east only.
            void Boundary(int id, float x, float y) =>
                def.nodes.Add(new NodeDef { id = id, x = x, y = y, isBoundary = true });

            for (int r = 0; r < rows; r++)
            {
                float sp = r == midRow ? FAST : SLOW;
                Boundary(WBound(r), -spacing, r * spacing);
                Boundary(EBound(r), cols * spacing, r * spacing);
                if (r == OneWayRow)
                {
                    Add(WBound(r), Id(r, 0), sp);                    // entry only
                    Add(Id(r, cols - 1), EBound(r), sp);             // exit only
                }
                else
                {
                    Add(WBound(r), Id(r, 0), sp); Add(Id(r, 0), WBound(r), sp);
                    Add(Id(r, cols - 1), EBound(r), sp); Add(EBound(r), Id(r, cols - 1), sp);
                }
            }
            for (int c = 0; c < cols; c++)
            {
                float sp = c == midCol ? FAST : SLOW;
                Boundary(SBound(c), c * spacing, -spacing);
                Boundary(NBound(c), c * spacing, rows * spacing);
                Add(SBound(c), Id(0, c), sp); Add(Id(0, c), SBound(c), sp);
                Add(Id(rows - 1, c), NBound(c), sp); Add(NBound(c), Id(rows - 1, c), sp);
            }

            DeriveTwoPhase(def, cols, rows);
            return def;
        }

        /// <summary>EW-green then NS-green per intersection, classified by the
        /// bearing of each in-link's upstream node — identical rule to the grid,
        /// so one-way approaches and missing arms fall out correctly.</summary>
        private static void DeriveTwoPhase(NetworkDef def, int cols, int rows)
        {
            var net = RoadNetwork.Build(def);
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int id = r * cols + c;
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
                def.nodes.Find(x => x.id == id).phases =
                    new List<PhaseDef> { Make(ew), Make(ns) };
            }
        }

        /// <summary>
        /// Corridor demand: two thoroughfares plus light cross traffic.
        ///   * heavy two-way flow along the primary (middle-row) arterial,
        ///   * moderate two-way flow along the secondary (middle-col) arterial,
        ///   * a one-way eastbound trickle down the bottom side street,
        ///   * light random background between the remaining boundaries.
        /// `rush`=true replaces the constant arterial rates with a rush-hour
        /// curve (ramp up, peak, drain) over `period` seconds so the policy must
        /// track a non-stationary load, not just a fixed one.
        /// </summary>
        public static DemandDef CorridorDemand(int cols, int rows, float scale = 1f,
                                               bool rush = false, float period = 600f,
                                               ulong seed = 11)
        {
            var def = new DemandDef();
            int midRow = MidRow(rows), midCol = MidCol(cols);

            RateCurve Rush(float peak)
            {
                if (!rush) return RateCurve.Constant(peak * scale);
                float lo = peak * 0.15f * scale, hi = peak * scale;
                return new RateCurve {
                    times = { 0f, period * 0.30f, period * 0.55f, period },
                    rates = { lo, hi, hi, lo } };
            }
            void Flow(int o, int d, RateCurve rate) =>
                def.flows.Add(new OdFlowDef { origin = o, dest = d, rate = rate });

            // Primary arterial: the busy east-west thoroughfare, both directions.
            Flow(WBound(midRow), EBound(midRow), Rush(26f));
            Flow(EBound(midRow), WBound(midRow), Rush(22f));
            // Secondary arterial: north-south, moderate, both directions.
            Flow(SBound(midCol), NBound(midCol), Rush(14f));
            Flow(NBound(midCol), SBound(midCol), Rush(12f));
            // One-way side street: eastbound trickle that can only exit east.
            Flow(WBound(OneWayRow), EBound(OneWayRow), RateCurve.Constant(6f * scale));

            // Light background: random perimeter OD, skipping the one-way row's
            // sink-only / entry-only ends so every authored flow is routable.
            var boundaries = new List<int>();
            for (int r = 0; r < rows; r++)
            {
                if (r != OneWayRow) boundaries.Add(WBound(r));
                boundaries.Add(EBound(r));
            }
            for (int c = 0; c < cols; c++) { boundaries.Add(SBound(c)); boundaries.Add(NBound(c)); }
            var rng = new Rng(seed);
            int pairs = Math.Min(boundaries.Count * 2, 60);
            for (int i = 0; i < pairs; i++)
            {
                int o = boundaries[rng.RangeInt(0, boundaries.Count)];
                int d = boundaries[rng.RangeInt(0, boundaries.Count)];
                if (o == d) { i--; continue; }
                Flow(o, d, RateCurve.Constant(2.5f * scale));
            }
            return def;
        }
    }
}
