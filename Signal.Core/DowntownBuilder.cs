using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// A configurable city grid: every street carries a direction (two-way, or
    /// one-way along either sign of its axis) and a turn mask (what a vehicle on
    /// that street may do at the next intersection). This is the substrate for
    /// the one-way-couplet / turn-restricted scenarios; the odd-shaped ones
    /// (wishbone, stagger, ramp) get bespoke builders that reuse the same phase
    /// derivation and boundary helpers.
    ///
    /// Directions: Fwd = increasing coordinate (East for rows, North for cols),
    /// Bwd = decreasing. Turn mask is applied to the links ON that street, so
    /// `Through|Right` = "no left turns off this street" and `Right` = right-only.
    /// Node ids r*cols+c; boundaries 3000+r (W), 3100+r (E), 3200+c (S), 3300+c (N).
    /// </summary>
    public enum Flow { TwoWay, Fwd, Bwd }

    public sealed class StreetSpec
    {
        public Flow flow = Flow.TwoWay;
        public TurnMask turns = TurnMask.All;
        public bool arterial = false;
        public StreetSpec() { }
        public StreetSpec(Flow f, TurnMask t = TurnMask.All, bool art = false)
        { flow = f; turns = t; arterial = art; }
    }

    public static class DowntownBuilder
    {
        public const float FAST = 16.7f, SLOW = 11.1f;
        public static int WB(int r) => 3000 + r;
        public static int EB(int r) => 3100 + r;
        public static int SB(int c) => 3200 + c;
        public static int NB(int c) => 3300 + c;

        public static NetworkDef Build(int cols, int rows, StreetSpec[] hRows,
            StreetSpec[] vCols, Dictionary<int, ControlType> control = null,
            float spacing = 200f)
        {
            var def = new NetworkDef();
            int linkId = 1;
            void Add(int from, int to, TurnMask turns, float speed)
                => def.links.Add(new LinkDef { id = linkId++, from = from, to = to,
                                               length = spacing, speedLimit = speed, turns = turns });
            int Id(int r, int c) => r * cols + c;

            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var ct = ControlType.Signalized;
                if (control != null && control.TryGetValue(Id(r, c), out var ov)) ct = ov;
                def.nodes.Add(new NodeDef { id = Id(r, c), x = c * spacing, y = r * spacing, control = ct });
            }

            // Horizontal streets.
            for (int r = 0; r < rows; r++)
            {
                var s = hRows[r]; float sp = s.arterial ? FAST : SLOW;
                for (int c = 0; c < cols - 1; c++)
                {
                    if (s.flow != Flow.Bwd) Add(Id(r, c), Id(r, c + 1), s.turns, sp);       // E
                    if (s.flow != Flow.Fwd) Add(Id(r, c + 1), Id(r, c), s.turns, sp);       // W
                }
            }
            // Vertical streets.
            for (int c = 0; c < cols; c++)
            {
                var s = vCols[c]; float sp = s.arterial ? FAST : SLOW;
                for (int r = 0; r < rows - 1; r++)
                {
                    if (s.flow != Flow.Bwd) Add(Id(r, c), Id(r + 1, c), s.turns, sp);       // N
                    if (s.flow != Flow.Fwd) Add(Id(r + 1, c), Id(r, c), s.turns, sp);       // S
                }
            }

            // Boundary stubs, direction-aware: an inbound stub carries the street's
            // turn mask; leaving the grid is unrestricted.
            void Boundary(int id, float x, float y)
                => def.nodes.Add(new NodeDef { id = id, x = x, y = y, isBoundary = true });
            for (int r = 0; r < rows; r++)
            {
                var s = hRows[r]; float sp = s.arterial ? FAST : SLOW;
                Boundary(WB(r), -spacing, r * spacing); Boundary(EB(r), cols * spacing, r * spacing);
                if (s.flow != Flow.Bwd) Add(WB(r), Id(r, 0), s.turns, sp);        // enter west
                if (s.flow != Flow.Fwd) Add(Id(r, 0), WB(r), TurnMask.All, sp);   // exit west
                if (s.flow != Flow.Bwd) Add(Id(r, cols - 1), EB(r), TurnMask.All, sp); // exit east
                if (s.flow != Flow.Fwd) Add(EB(r), Id(r, cols - 1), s.turns, sp); // enter east
            }
            for (int c = 0; c < cols; c++)
            {
                var s = vCols[c]; float sp = s.arterial ? FAST : SLOW;
                Boundary(SB(c), c * spacing, -spacing); Boundary(NB(c), c * spacing, rows * spacing);
                if (s.flow != Flow.Bwd) Add(SB(c), Id(0, c), s.turns, sp);        // enter south
                if (s.flow != Flow.Fwd) Add(Id(0, c), SB(c), TurnMask.All, sp);   // exit south
                if (s.flow != Flow.Bwd) Add(Id(rows - 1, c), NB(c), TurnMask.All, sp); // exit north
                if (s.flow != Flow.Fwd) Add(NB(c), Id(rows - 1, c), s.turns, sp); // enter north
            }

            DeriveTwoPhase(def);
            return def;
        }

        /// <summary>EW-green then NS-green per signalized node, from real movements
        /// (already turn-filtered). An axis with no movements yields an empty phase,
        /// which is dropped so the node's phase list is never degenerate.</summary>
        public static void DeriveTwoPhase(NetworkDef def)
        {
            var net = RoadNetwork.Build(def);
            foreach (var nd in def.nodes)
            {
                if (nd.control != ControlType.Signalized) continue;
                var node = net.NodeById(nd.id);
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
                var phases = new List<PhaseDef>();
                var pew = Make(ew); var pns = Make(ns);
                if (pew.movements.Count > 0) phases.Add(pew);
                if (pns.movements.Count > 0) phases.Add(pns);
                if (phases.Count == 0) phases.Add(pew);   // isolated node: keep one
                nd.phases = phases;
            }
        }

        // -- demand helpers --------------------------------------------------

        /// <summary>Override the turn mask on a specific approach link (the lane
        /// entering `to` from `from`). Used for per-node rules like RIRO that a
        /// whole-street mask can't express. Call DeriveTwoPhase afterwards.</summary>
        public static void SetLinkTurns(NetworkDef def, int from, int to, TurnMask t)
        {
            var l = def.links.Find(x => x.from == from && x.to == to);
            if (l != null) l.turns = t;
        }

        public static OdFlowDef F(int o, int d, float rate)
            => new OdFlowDef { origin = o, dest = d, rate = RateCurve.Constant(rate) };

        public static OdFlowDef FRush(int o, int d, float peak, float period = 600f, float loFrac = 0.15f)
            => new OdFlowDef { origin = o, dest = d, rate = new RateCurve {
                   times = { 0f, period * 0.3f, period * 0.55f, period },
                   rates = { peak * loFrac, peak, peak, peak * loFrac } } };

        /// <summary>Which boundaries can be an origin (traffic enters the grid there)
        /// and which can be a destination (traffic can exit there), given the street
        /// directions — so authored demand never picks an unroutable endpoint.</summary>
        public static void EntriesExits(int cols, int rows, StreetSpec[] hRows,
            StreetSpec[] vCols, out List<int> entries, out List<int> exits)
        {
            entries = new List<int>(); exits = new List<int>();
            for (int r = 0; r < rows; r++)
            {
                var f = hRows[r].flow;
                if (f != Flow.Bwd) { entries.Add(WB(r)); exits.Add(EB(r)); }   // eastbound present
                if (f != Flow.Fwd) { entries.Add(EB(r)); exits.Add(WB(r)); }   // westbound present
            }
            for (int c = 0; c < cols; c++)
            {
                var f = vCols[c].flow;
                if (f != Flow.Bwd) { entries.Add(SB(c)); exits.Add(NB(c)); }   // northbound present
                if (f != Flow.Fwd) { entries.Add(NB(c)); exits.Add(SB(c)); }   // southbound present
            }
        }

        /// <summary>Light random background OD among the given boundaries.</summary>
        public static void AddBackground(DemandDef d, List<int> origins, List<int> dests,
                                         float perFlow, int pairs, ulong seed = 17)
        {
            var rng = new Rng(seed);
            for (int i = 0; i < pairs; i++)
            {
                int o = origins[rng.RangeInt(0, origins.Count)];
                int dd = dests[rng.RangeInt(0, dests.Count)];
                if (o == dd) { i--; continue; }
                d.flows.Add(F(o, dd, perFlow));
            }
        }
    }
}
