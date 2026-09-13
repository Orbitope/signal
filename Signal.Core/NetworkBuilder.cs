using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// Programmatic builders for canonical test networks. All four-arm variants
    /// share boundary node ids (100=N, 101=E, 102=S, 103=W) so the SAME DemandDef
    /// runs against every control type — that's what makes the control-type
    /// benchmark (stop sign vs signal vs roundabout) an apples-to-apples comparison.
    /// </summary>
    public static class NetworkBuilder
    {
        public const int N = 100, E = 101, S = 102, W = 103;
        public const int Center = 0;

        // -----------------------------------------------------------------
        //  Four-way: one central node, 4 boundary arms, 8 links (in/out per arm).
        //  Link ids: in-links N=1,E=2,S=3,W=4; out-links N=11,E=12,S=13,W=14.
        // -----------------------------------------------------------------
        public static NetworkDef FourWay(ControlType control, float armLength = 150f)
        {
            var def = new NetworkDef();
            def.nodes.Add(new NodeDef { id = Center, x = 0, y = 0, control = control });
            def.nodes.Add(new NodeDef { id = N, x = 0, y = armLength, isBoundary = true });
            def.nodes.Add(new NodeDef { id = E, x = armLength, y = 0, isBoundary = true });
            def.nodes.Add(new NodeDef { id = S, x = 0, y = -armLength, isBoundary = true });
            def.nodes.Add(new NodeDef { id = W, x = -armLength, y = 0, isBoundary = true });

            int[] arms = { N, E, S, W };
            for (int i = 0; i < 4; i++)
            {
                def.links.Add(new LinkDef { id = 1 + i, from = arms[i], to = Center, length = armLength });
                def.links.Add(new LinkDef { id = 11 + i, from = Center, to = arms[i], length = armLength });
            }

            var center = def.nodes[0];
            switch (control)
            {
                case ControlType.Signalized:
                    center.phases = TwoPhasePermissiveLefts(def, Center);
                    break;
                case ControlType.TwoWayStop:
                    // East-west is the major road: in-links E(2) and W(4).
                    center.majorInLinks = new List<int> { 2, 4 };
                    break;
            }
            return def;
        }

        /// <summary>Standard 2-phase plan: NS movements together (lefts permissive),
        /// then EW. Derives movement indices from the built network so the phase
        /// definition can never drift from the geometry.</summary>
        public static List<PhaseDef> TwoPhasePermissiveLefts(NetworkDef def, int nodeId)
        {
            var net = RoadNetwork.Build(def);
            var node = net.NodeById(nodeId);
            var nsIn = new HashSet<int> { 1, 3 };   // link ids from N and S
            var ewIn = new HashSet<int> { 2, 4 };

            PhaseDef MakePhase(HashSet<int> inLinks)
            {
                var p = new PhaseDef();
                foreach (var m in node.Movements)
                {
                    if (!inLinks.Contains(m.InLink)) continue;
                    p.movements.Add(m.Index);
                    // A movement that conflicts with another movement in the same
                    // phase (i.e. the opposing left) must be permissive.
                    foreach (var other in node.Movements)
                        if (inLinks.Contains(other.InLink) && other.Index != m.Index &&
                            node.Conflicts[m.Index, other.Index] && IsLeftTurn(net, node, m))
                        { p.permissive.Add(m.Index); break; }
                }
                return p;
            }
            return new List<PhaseDef> { MakePhase(nsIn), MakePhase(ewIn) };
        }

        /// <summary>Left turn test — movements now carry their classification.</summary>
        public static bool IsLeftTurn(RoadNetwork net, Node node, Movement m)
            => m.Turn == TurnMask.Left;

        // -----------------------------------------------------------------
        //  Arterial: K signalized four-ways in a row along the x axis, with
        //  N/S stub arms at each. Boundary ids: west end=200, east end=201,
        //  north stubs=300+i, south stubs=400+i. Center nodes: 0..K-1.
        // -----------------------------------------------------------------
        public static NetworkDef Arterial(int k, float spacing = 200f, float stub = 120f)
        {
            var def = new NetworkDef();
            int linkId = 1;
            int LinkAdd(int from, int to, float len)
            { def.links.Add(new LinkDef { id = linkId, from = from, to = to, length = len }); return linkId++; }

            for (int i = 0; i < k; i++)
                def.nodes.Add(new NodeDef { id = i, x = i * spacing, y = 0, control = ControlType.Signalized });
            def.nodes.Add(new NodeDef { id = 200, x = -spacing, y = 0, isBoundary = true });
            def.nodes.Add(new NodeDef { id = 201, x = k * spacing, y = 0, isBoundary = true });
            for (int i = 0; i < k; i++)
            {
                def.nodes.Add(new NodeDef { id = 300 + i, x = i * spacing, y = stub, isBoundary = true });
                def.nodes.Add(new NodeDef { id = 400 + i, x = i * spacing, y = -stub, isBoundary = true });
            }

            // Arterial links (both directions).
            LinkAdd(200, 0, spacing); LinkAdd(0, 200, spacing);
            for (int i = 0; i < k - 1; i++) { LinkAdd(i, i + 1, spacing); LinkAdd(i + 1, i, spacing); }
            LinkAdd(k - 1, 201, spacing); LinkAdd(201, k - 1, spacing);
            // Stubs.
            for (int i = 0; i < k; i++)
            {
                LinkAdd(300 + i, i, stub); LinkAdd(i, 300 + i, stub);
                LinkAdd(400 + i, i, stub); LinkAdd(i, 400 + i, stub);
            }

            // Phases per intersection, derived from the actual geometry.
            var net = RoadNetwork.Build(def);
            for (int i = 0; i < k; i++)
            {
                var node = net.NodeById(i);
                var ewIn = new List<int>(); var nsIn = new List<int>();
                foreach (int inId in node.InLinks)
                {
                    var l = def.links.Find(x => x.id == inId);
                    var from = def.nodes.Find(x => x.id == l.from);
                    if (Math.Abs(from.y) > 1f) nsIn.Add(inId); else ewIn.Add(inId);
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
                                    node.Conflicts[m.Index, o.Index] && IsLeftTurn(net, node, m))
                                { p.permissive.Add(m.Index); break; }
                        }
                    return p;
                }
                def.nodes.Find(x => x.id == i).phases = new List<PhaseDef> { Make(ewIn), Make(nsIn) };
            }
            return def;
        }

        // -----------------------------------------------------------------
        //  Roundabout: 4 merge nodes on a circle joined by one-way arcs,
        //  4 approach arms (same boundary ids as FourWay). Entry movements
        //  yield to the circulating arc via YieldEntry.
        //  Merge nodes: 10..13 (NE order: node 10 receives N approach, etc.)
        // -----------------------------------------------------------------
        public static NetworkDef Roundabout(float armLength = 150f, float radius = 15f)
        {
            var def = new NetworkDef();
            float arc = (float)(Math.PI / 2 * radius);   // quarter-circle arc length
            int[] merges = { 10, 11, 12, 13 };           // N, E, S, W positions
            (float x, float y)[] mpos = { (0, radius), (radius, 0), (0, -radius), (-radius, 0) };
            int[] arms = { N, E, S, W };
            (float x, float y)[] apos = { (0, armLength), (armLength, 0), (0, -armLength), (-armLength, 0) };

            for (int i = 0; i < 4; i++)
            {
                def.nodes.Add(new NodeDef { id = merges[i], x = mpos[i].x, y = mpos[i].y, control = ControlType.YieldEntry });
                def.nodes.Add(new NodeDef { id = arms[i], x = apos[i].x, y = apos[i].y, isBoundary = true });
            }

            int linkId = 1;
            int LinkAdd(int from, int to, float len, float speed = 8f)
            { def.links.Add(new LinkDef { id = linkId, from = from, to = to, length = len, speedLimit = speed }); return linkId++; }

            // Circulating arcs, counterclockwise: N->W->S->E->N (10->13->12->11->10).
            var arcIn = new Dictionary<int, int>();      // merge node -> incoming arc link id
            arcIn[13] = LinkAdd(10, 13, arc);
            arcIn[12] = LinkAdd(13, 12, arc);
            arcIn[11] = LinkAdd(12, 11, arc);
            arcIn[10] = LinkAdd(11, 10, arc);
            // Approaches and exits (full speed on arms).
            for (int i = 0; i < 4; i++)
            {
                LinkAdd(arms[i], merges[i], armLength - radius, 13.9f);
                LinkAdd(merges[i], arms[i], armLength - radius, 13.9f);
            }

            // Each merge yields to its incoming arc.
            foreach (var m in merges)
                def.nodes.Find(x => x.id == m).majorInLinks = new List<int> { arcIn[m] };
            return def;
        }

        // -----------------------------------------------------------------
        //  Canonical symmetric demand for the four-arm layouts: every arm to
        //  every other arm at equal rates. totalVehPerMin is network-wide.
        // -----------------------------------------------------------------
        public static DemandDef SymmetricDemand(float totalVehPerMin)
        {
            var def = new DemandDef();
            int[] arms = { N, E, S, W };
            float per = totalVehPerMin / 12f;            // 4 origins x 3 destinations
            foreach (var o in arms) foreach (var d in arms)
                if (o != d) def.flows.Add(new OdFlowDef { origin = o, dest = d, rate = RateCurve.Constant(per) });
            return def;
        }

        /// <summary>Asymmetric demand: one dominant flow (N->S) plus light background.
        /// The pattern that breaks roundabouts.</summary>
        public static DemandDef DominantFlowDemand(float dominantVehPerMin, float backgroundTotal)
        {
            var def = SymmetricDemand(backgroundTotal);
            def.flows.Add(new OdFlowDef { origin = N, dest = S, rate = RateCurve.Constant(dominantVehPerMin) });
            return def;
        }
    }
}
