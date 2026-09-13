using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>A turn movement: enter the node on InLink, leave on OutLink.</summary>
    public sealed class Movement
    {
        public int Index;          // index within owning node's movement list
        public int InLink, OutLink;
        public TurnMask Turn;      // Left, Through, or Right (single flag)
    }

    public sealed class Link
    {
        public int Id;
        public int From, To;
        public float Length;
        public float SpeedLimit;
        public TurnMask Turns = TurnMask.All;
        /// <summary>Vehicles ordered FRONT-FIRST (largest Pos first). Single lane in v1.</summary>
        public readonly List<Vehicle> Vehicles = new List<Vehicle>();

        public float OccupiedLength()
        {
            float sum = 0f;
            for (int i = 0; i < Vehicles.Count; i++) sum += Vehicles[i].Length + SimConfig.JamGap;
            return sum;
        }

        public bool HasEntrySpace(float vehLen)
        {
            // Space at the upstream end: no vehicle within (vehLen + gap) of pos 0,
            // and total occupancy leaves room.
            if (Vehicles.Count > 0)
            {
                var last = Vehicles[Vehicles.Count - 1];
                if (last.Pos < vehLen + SimConfig.JamGap) return false;
            }
            return OccupiedLength() + vehLen + SimConfig.JamGap <= Length;
        }

        public Vehicle Front => Vehicles.Count > 0 ? Vehicles[0] : null;

        public int QueueCount(float speedThreshold = 2f)
        {
            int n = 0;
            for (int i = 0; i < Vehicles.Count; i++)
                if (Vehicles[i].Speed < speedThreshold) n++;
            return n;
        }
    }

    public sealed class Node
    {
        public int Id;
        public float X, Y;
        public bool IsBoundary;
        public readonly List<int> InLinks = new List<int>();
        public readonly List<int> OutLinks = new List<int>();
        public readonly List<Movement> Movements = new List<Movement>();
        /// <summary>Conflicts[a][b] true iff movements a and b may not proceed simultaneously.</summary>
        public bool[,] Conflicts;
        public IIntersectionControl Control;   // assigned by Simulation from NodeDef

        public Movement FindMovement(int inLink, int outLink)
        {
            for (int i = 0; i < Movements.Count; i++)
                if (Movements[i].InLink == inLink && Movements[i].OutLink == outLink)
                    return Movements[i];
            return null;
        }
    }

    public sealed class RoadNetwork
    {
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Link> Links = new List<Link>();
        private readonly Dictionary<int, Node> _nodeById = new Dictionary<int, Node>();
        private readonly Dictionary<int, Link> _linkById = new Dictionary<int, Link>();

        public Node NodeById(int id) => _nodeById[id];
        public Link LinkById(int id) => _linkById[id];
        public bool TryNode(int id, out Node n) => _nodeById.TryGetValue(id, out n);
        public bool TryLink(int id, out Link l) => _linkById.TryGetValue(id, out l);

        public static RoadNetwork Build(NetworkDef def)
        {
            var net = new RoadNetwork();
            foreach (var nd in def.nodes)
            {
                var n = new Node { Id = nd.id, X = nd.x, Y = nd.y, IsBoundary = nd.isBoundary };
                net.Nodes.Add(n); net._nodeById[nd.id] = n;
            }
            foreach (var ld in def.links)
            {
                var l = new Link { Id = ld.id, From = ld.from, To = ld.to, Length = ld.length, SpeedLimit = ld.speedLimit, Turns = ld.turns };
                net.Links.Add(l); net._linkById[ld.id] = l;
                net._nodeById[ld.from].OutLinks.Add(l.Id);
                net._nodeById[ld.to].InLinks.Add(l.Id);
            }
            foreach (var n in net.Nodes)
            {
                DeriveMovements(net, n);
                BuildConflictMatrix(net, n);
            }
            return net;
        }

        /// <summary>All (in,out) pairs except U-turns, filtered by the in-lane's
        /// turn mask. A bay link (turns=Left) yields only left movements — this
        /// is where lane discipline is enforced, once, structurally.</summary>
        private static void DeriveMovements(RoadNetwork net, Node n)
        {
            n.Movements.Clear();
            foreach (int inId in n.InLinks)
            {
                var inL = net.LinkById(inId);
                foreach (int outId in n.OutLinks)
                {
                    var outL = net.LinkById(outId);
                    if (outL.To == inL.From) continue;   // U-turn: reverse of the arrival link
                    var turn = ClassifyTurn(net, n, inL, outL);
                    if ((inL.Turns & turn) == 0) continue;
                    n.Movements.Add(new Movement
                    { Index = n.Movements.Count, InLink = inId, OutLink = outId, Turn = turn });
                }
            }
        }

        /// <summary>Left/Through/Right from the cross and dot products of the
        /// arrival and departure directions. A geometric reversal (heading back
        /// the way you came, e.g. fork-lane → own approach's out-arm) classifies
        /// as None, and the turn-mask filter removes it — the structural U-turn
        /// rule alone can't catch these once forks exist, because the in-link's
        /// From is the fork, not the origin arm. Pass-through fork/merge
        /// geometry (near-parallel, forward) classifies as Through.</summary>
        public static TurnMask ClassifyTurn(RoadNetwork net, Node n, Link inL, Link outL)
        {
            var a = net.NodeById(inL.From); var b = net.NodeById(outL.To);
            float inDx = n.X - a.X, inDy = n.Y - a.Y;
            float outDx = b.X - n.X, outDy = b.Y - n.Y;
            float cross = inDx * outDy - inDy * outDx;
            float dot = inDx * outDx + inDy * outDy;
            if (dot < 0 && Math.Abs(cross) < Math.Abs(dot))
                return TurnMask.None;                       // reversal = U-turn
            if (Math.Abs(cross) <= Math.Abs(dot) * 0.4f)
                return TurnMask.Through;
            return cross > 0 ? TurnMask.Left : TurnMask.Right;
        }

        // -----------------------------------------------------------------
        // Conflict matrix: place each connected link on a unit circle around
        // the node at its bearing, laterally offset for right-hand traffic
        // (arrivals sit left of the arm's ray from the node's viewpoint,
        // departures right) — without this, an arm's fork point and boundary
        // point collapse to one spot and opposing movements falsely "share"
        // endpoints. A movement is the chord from its in-point to its
        // out-point. Two movements conflict iff their chords properly
        // intersect, or they merge (same out-link) — EXCEPT movements from
        // the same approach (same origin node), which share a signal
        // indication in reality and never conflict; two lanes discharging
        // into one exit is a lane drop, not a signal conflict.
        // -----------------------------------------------------------------
        private static void BuildConflictMatrix(RoadNetwork net, Node n)
        {
            int m = n.Movements.Count;
            n.Conflicts = new bool[m, m];
            if (m < 2) return;

            const double LateralOffset = 0.15;   // radians of lane separation

            (double x, double y) PointFor(int linkId, bool incoming)
            {
                var l = net.LinkById(linkId);
                var other = net.NodeById(incoming ? l.From : l.To);
                double dx = other.X - n.X, dy = other.Y - n.Y;
                double ang = Math.Atan2(dy, dx) + (incoming ? LateralOffset : -LateralOffset);
                return (Math.Cos(ang), Math.Sin(ang));
            }

            for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
            {
                var ma = n.Movements[a]; var mb = n.Movements[b];
                var inA = net.LinkById(ma.InLink); var inB = net.LinkById(mb.InLink);
                bool conflict;
                if (inA.From == inB.From) conflict = false;             // same approach
                else if (ma.OutLink == mb.OutLink) conflict = true;     // cross-approach merge
                else
                {
                    var a1 = PointFor(ma.InLink, true);  var a2 = PointFor(ma.OutLink, false);
                    var b1 = PointFor(mb.InLink, true);  var b2 = PointFor(mb.OutLink, false);
                    conflict = SegmentsIntersect(a1, a2, b1, b2);
                }
                n.Conflicts[a, b] = conflict;
                n.Conflicts[b, a] = conflict;
            }
        }

        private static bool SegmentsIntersect((double x, double y) p1, (double x, double y) p2,
                                              (double x, double y) p3, (double x, double y) p4)
        {
            double D((double x, double y) o, (double x, double y) u, (double x, double y) v)
                => (u.x - o.x) * (v.y - o.y) - (u.y - o.y) * (v.x - o.x);
            double d1 = D(p3, p4, p1), d2 = D(p3, p4, p2), d3 = D(p1, p2, p3), d4 = D(p1, p2, p4);
            // Proper intersection only; shared endpoints (handled above) don't count.
            return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
                   ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
        }

        /// <summary>Throws if any phase contains two conflicting movements.
        /// Run at load, i.e. authoring bugs fail fast, never at runtime.</summary>
        public void ValidatePhases(Node n, List<PhaseDef> phases)
        {
            for (int p = 0; p < phases.Count; p++)
            {
                var mv = phases[p].movements;
                for (int i = 0; i < mv.Count; i++)
                for (int j = i + 1; j < mv.Count; j++)
                {
                    bool bothProtected = !phases[p].permissive.Contains(mv[i]) &&
                                         !phases[p].permissive.Contains(mv[j]);
                    if (n.Conflicts[mv[i], mv[j]] && bothProtected)
                        throw new InvalidOperationException(
                            $"Node {n.Id} phase {p}: movements {mv[i]} and {mv[j]} conflict and are both protected.");
                }
            }
        }
    }
}
