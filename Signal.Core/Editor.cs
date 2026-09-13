using System;
using System.Collections.Generic;

namespace Signal.Core
{
    // =====================================================================
    //  The editor's document (GAME_PLAN §6). Grid-snapped: junctions sit on
    //  cells, streets join neighbouring cells, an arm with no neighbour can
    //  open to the outside (an entry/exit). Everything a player can do to
    //  a junction or a street beyond that (roundabout, bays, turn bans,
    //  timed plans, one-way) is the same EditOp list puzzles use, applied
    //  on top of the built level. Plain fields; JSON round-trips.
    // =====================================================================

    [Serializable] public class JunctionDef
    {
        public int gx, gy;
        public ControlType control = ControlType.Signalized;
        public int majorAxis = 0;                       // TwoWayStop: 0 = E-W keeps priority
        public bool openN, openE, openS, openW;         // arm to the outside (entry + exit)

        public bool Open(int dir) => dir switch { 0 => openN, 1 => openE, 2 => openS, _ => openW };
        public void SetOpen(int dir, bool v) { switch (dir) { case 0: openN = v; break; case 1: openE = v; break; case 2: openS = v; break; default: openW = v; break; } }
    }

    [Serializable] public class StreetDef
    {
        public int ax, ay, bx, by;                      // neighbouring cells, a < b in reading order
        public bool ab = true, ba = true;               // which directions exist
    }

    [Serializable] public class ArmWeight
    {
        public int gx, gy, dir;                         // an open arm
        public float weight = 1f;                       // multiplies every flow starting or ending there
    }

    [Serializable] public class DemandSpec
    {
        public string preset = "balanced";              // balanced | east-west | north-south
        public float total = 24f;                       // veh/min over the whole map
        public bool rush;                               // peak in the middle third
        public List<ArmWeight> arms = new List<ArmWeight>();   // asymmetry: a busy side street, a quiet one

        public float ArmWeightOf(int boundaryId)
        {
            foreach (var a in arms) if (EditorDoc.BoundaryId(a.gx, a.gy, a.dir) == boundaryId) return a.weight;
            return 1f;
        }

        public DemandSpec Weigh(int gx, int gy, int dir, float weight)
        {
            arms.Add(new ArmWeight { gx = gx, gy = gy, dir = dir, weight = weight });
            return this;
        }
    }

    [Serializable] public class EditorDoc
    {
        public string name = "untitled";
        public float spacing = 200f;                    // m between cells
        public float stub = 150f;                       // m from a junction to the map edge
        public float duration = 300f;
        public int cols = 6, rows = 4;
        public List<JunctionDef> junctions = new List<JunctionDef>();
        public List<StreetDef> streets = new List<StreetDef>();
        public DemandSpec demand = new DemandSpec();
        public List<EditOp> ops = new List<EditOp>();

        // ------------------------------------------------------------ ids
        // Deterministic ids so ops (which name nodes and links) survive edits.
        public static int Cell(int gx, int gy) => gx * 100 + gy;
        public static int JunctionId(int gx, int gy) => 10000 + Cell(gx, gy);
        public static int BoundaryId(int gx, int gy, int dir) => 20000 + Cell(gx, gy) * 4 + dir;
        public static int StreetLinkId(int gx, int gy, int dir) => 100000 + Cell(gx, gy) * 4 + dir;   // junction -> neighbour in dir
        public static int EntryLinkId(int gx, int gy, int dir) => 200000 + Cell(gx, gy) * 4 + dir;    // outside -> junction
        public static int ExitLinkId(int gx, int gy, int dir) => 300000 + Cell(gx, gy) * 4 + dir;     // junction -> outside
        public static readonly int[] DX = { 0, 1, 0, -1 }, DY = { -1, 0, 1, 0 };   // N, E, S, W in grid (gy grows south)
        public static readonly string[] DirName = { "north", "east", "south", "west" };

        // ------------------------------------------------------------ queries

        public JunctionDef JunctionAt(int gx, int gy) => junctions.Find(j => j.gx == gx && j.gy == gy);

        public StreetDef StreetBetween(int ax, int ay, int bx, int by)
        {
            Order(ref ax, ref ay, ref bx, ref by);
            return streets.Find(s => s.ax == ax && s.ay == ay && s.bx == bx && s.by == by);
        }

        static void Order(ref int ax, ref int ay, ref int bx, ref int by)
        {
            if (ay > by || (ay == by && ax > bx)) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
        }

        public static bool Adjacent(int ax, int ay, int bx, int by)
            => Math.Abs(ax - bx) + Math.Abs(ay - by) == 1;

        /// <summary>Neighbour junction in a direction, if a street links them.</summary>
        public JunctionDef NeighbourVia(JunctionDef j, int dir, out StreetDef street)
        {
            street = StreetBetween(j.gx, j.gy, j.gx + DX[dir], j.gy + DY[dir]);
            return street == null ? null : JunctionAt(j.gx + DX[dir], j.gy + DY[dir]);
        }

        // ------------------------------------------------------------ edits

        public JunctionDef AddJunction(int gx, int gy)
        {
            var j = JunctionAt(gx, gy);
            if (j != null) return j;
            j = new JunctionDef { gx = gx, gy = gy };
            junctions.Add(j);
            return j;
        }

        public void RemoveJunction(int gx, int gy)
        {
            var j = JunctionAt(gx, gy);
            if (j == null) return;
            junctions.Remove(j);
            streets.RemoveAll(s => (s.ax == gx && s.ay == gy) || (s.bx == gx && s.by == gy));
            int id = JunctionId(gx, gy);
            ops.RemoveAll(o => o.node == id || LinkTouches(o.link, gx, gy));
        }

        static bool LinkTouches(int linkId, int gx, int gy)
        {
            if (linkId < 0) return false;
            int cell = (linkId % 100000) / 4;
            return cell == Cell(gx, gy);
        }

        /// <summary>Connect two neighbouring junctions two-way (no-op if it exists).</summary>
        public StreetDef Connect(int ax, int ay, int bx, int by)
        {
            if (!Adjacent(ax, ay, bx, by) || JunctionAt(ax, ay) == null || JunctionAt(bx, by) == null) return null;
            var s = StreetBetween(ax, ay, bx, by);
            if (s != null) return s;
            Order(ref ax, ref ay, ref bx, ref by);
            s = new StreetDef { ax = ax, ay = ay, bx = bx, by = by };
            streets.Add(s);
            // A street between two junctions closes any "outside" arm on that side.
            JunctionAt(ax, ay).SetOpen(DirFrom(ax, ay, bx, by), false);
            JunctionAt(bx, by).SetOpen(DirFrom(bx, by, ax, ay), false);
            return s;
        }

        public void Disconnect(int ax, int ay, int bx, int by)
        {
            var s = StreetBetween(ax, ay, bx, by);
            if (s == null) return;
            streets.Remove(s);
            int la = StreetLinkId(s.ax, s.ay, DirFrom(s.ax, s.ay, s.bx, s.by));
            int lb = StreetLinkId(s.bx, s.by, DirFrom(s.bx, s.by, s.ax, s.ay));
            ops.RemoveAll(o => o.link == la || o.link == lb);
        }

        public static int DirFrom(int ax, int ay, int bx, int by)
        {
            for (int d = 0; d < 4; d++) if (ax + DX[d] == bx && ay + DY[d] == by) return d;
            return -1;
        }

        // ------------------------------------------------------------ build

        /// <summary>Human-readable problems that would stop Build. Empty = fine.</summary>
        public List<string> Problems()
        {
            var list = new List<string>();
            if (junctions.Count == 0) { list.Add("place at least one junction"); return list; }
            int entries = 0, exits = 0;
            foreach (var j in junctions)
            {
                int arms = 0;
                for (int d = 0; d < 4; d++)
                {
                    if (NeighbourVia(j, d, out _) != null) arms++;
                    else if (j.Open(d)) { arms++; entries++; exits++; }
                }
                if (arms < 2) list.Add($"junction ({j.gx},{j.gy}) needs at least two streets");
            }
            if (entries == 0) list.Add("open at least one arm to the outside so traffic can enter");
            return list;
        }

        /// <summary>The playable level: grid geometry, controls, demand, then the op list.</summary>
        public LevelDef Build() => Edits.Apply(BuildRaw(), ops);

        /// <summary>Grid geometry, controls and demand only: no ops, no validation.</summary>
        public LevelDef BuildRaw()
        {
            var problems = Problems();
            if (problems.Count > 0) throw new PuzzleException(problems[0]);

            var lv = new LevelDef { name = name, duration = duration };
            var net = lv.network;
            foreach (var j in junctions)
            {
                net.nodes.Add(new NodeDef { id = JunctionId(j.gx, j.gy), x = j.gx * spacing, y = -j.gy * spacing, control = j.control });
                for (int d = 0; d < 4; d++)
                {
                    var nb = NeighbourVia(j, d, out var s);
                    if (nb != null)
                    {
                        bool forward = (s.ax == j.gx && s.ay == j.gy) ? s.ab : s.ba;
                        if (forward)
                            net.links.Add(new LinkDef { id = StreetLinkId(j.gx, j.gy, d), from = JunctionId(j.gx, j.gy), to = JunctionId(nb.gx, nb.gy), length = spacing });
                    }
                    else if (j.Open(d))
                    {
                        int b = BoundaryId(j.gx, j.gy, d);
                        net.nodes.Add(new NodeDef { id = b, x = (j.gx + DX[d]) * spacing - DX[d] * (spacing - stub), y = -((j.gy + DY[d]) * spacing - DY[d] * (spacing - stub)), isBoundary = true });
                        net.links.Add(new LinkDef { id = EntryLinkId(j.gx, j.gy, d), from = b, to = JunctionId(j.gx, j.gy), length = stub });
                        net.links.Add(new LinkDef { id = ExitLinkId(j.gx, j.gy, d), from = JunctionId(j.gx, j.gy), to = b, length = stub });
                    }
                }
            }
            foreach (var j in junctions)
            {
                var nd = net.nodes.Find(n => n.id == JunctionId(j.gx, j.gy));
                if (j.control == ControlType.Signalized) nd.phases = PhaseGen.Auto(net, nd.id);
                else if (j.control == ControlType.TwoWayStop || j.control == ControlType.YieldEntry)
                {
                    nd.majorInLinks = Edits.AxisInLinks(net, nd, j.majorAxis);
                    if (nd.majorInLinks.Count == 0) nd.majorInLinks = Edits.AxisInLinks(net, nd, 1 - j.majorAxis);
                }
                else if (j.control == ControlType.Uncontrolled) nd.control = ControlType.AllWayStop;   // never leave a junction lawless
            }
            lv.demand = BuildDemand(lv);
            return lv;
        }

        DemandDef BuildDemand(LevelDef lv)
        {
            var d = new DemandDef();
            var entries = new List<NodeDef>();
            foreach (var n in lv.network.nodes) if (n.isBoundary) entries.Add(n);
            if (entries.Count < 2) return d;
            // Weight each origin→destination pair by axis preference.
            float cx = 0, cy = 0;
            foreach (var n in lv.network.nodes) if (!n.isBoundary) { cx += n.x; cy += n.y; }
            cx /= Math.Max(1, junctions.Count); cy /= Math.Max(1, junctions.Count);
            var weights = new List<(NodeDef o, NodeDef t, float w)>();
            float sum = 0f;
            foreach (var o in entries) foreach (var t in entries)
            {
                if (o == t) continue;
                bool oEw = Math.Abs(o.x - cx) > Math.Abs(o.y - cy), tEw = Math.Abs(t.x - cx) > Math.Abs(t.y - cy);
                float w = 1f;
                if (demand.preset == "east-west") w = oEw && tEw ? 4f : oEw || tEw ? 1f : 0.5f;
                else if (demand.preset == "north-south") w = !oEw && !tEw ? 4f : !oEw || !tEw ? 1f : 0.5f;
                w *= demand.ArmWeightOf(o.id) * demand.ArmWeightOf(t.id);
                weights.Add((o, t, w)); sum += w;
            }
            foreach (var (o, t, w) in weights)
            {
                float rate = demand.total * w / sum;
                RateCurve curve = demand.rush
                    ? new RateCurve
                    {
                        times = { 0f, duration * 0.25f, duration * 0.4f, duration * 0.65f, duration * 0.8f, duration },
                        rates = { rate * 0.5f, rate * 0.5f, rate * 1.6f, rate * 1.6f, rate * 0.5f, rate * 0.5f }
                    }
                    : RateCurve.Constant(rate);
                d.flows.Add(new OdFlowDef { origin = o.id, dest = t.id, rate = curve });
            }
            return d;
        }

        // ------------------------------------------------------------ templates

        /// <summary>A starter: a 2x2 grid of signals, every outer arm open.</summary>
        public static EditorDoc Starter()
        {
            var doc = new EditorDoc { name = "my level" };
            for (int gx = 2; gx <= 3; gx++) for (int gy = 1; gy <= 2; gy++) doc.AddJunction(gx, gy);
            doc.Connect(2, 1, 3, 1); doc.Connect(2, 2, 3, 2); doc.Connect(2, 1, 2, 2); doc.Connect(3, 1, 3, 2);
            foreach (var j in doc.junctions)
                for (int d = 0; d < 4; d++)
                    if (doc.NeighbourVia(j, d, out _) == null) j.SetOpen(d, true);
            return doc;
        }

        public EditorDoc Clone()
        {
            var c = new EditorDoc { name = name, spacing = spacing, stub = stub, duration = duration, cols = cols, rows = rows };
            foreach (var j in junctions) c.junctions.Add(new JunctionDef { gx = j.gx, gy = j.gy, control = j.control, majorAxis = j.majorAxis, openN = j.openN, openE = j.openE, openS = j.openS, openW = j.openW });
            foreach (var s in streets) c.streets.Add(new StreetDef { ax = s.ax, ay = s.ay, bx = s.bx, by = s.by, ab = s.ab, ba = s.ba });
            c.demand = new DemandSpec { preset = demand.preset, total = demand.total, rush = demand.rush };
            foreach (var a in demand.arms) c.demand.arms.Add(new ArmWeight { gx = a.gx, gy = a.gy, dir = a.dir, weight = a.weight });
            foreach (var o in ops) c.ops.Add(o.Clone());
            return c;
        }
    }
}
