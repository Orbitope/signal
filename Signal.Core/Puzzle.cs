using System;
using System.Collections.Generic;

namespace Signal.Core
{
    // =====================================================================
    //  Puzzles (GAME_PLAN §2–§4). One data model: a puzzle is an authored
    //  LevelDef plus a PuzzleDef; the player's answer is a list of EditOps
    //  applied on top of the level. The editor uses the same ops with every
    //  tool switched on. Plain public fields, so JSON round-trips with
    //  IncludeFields like the rest of the defs.
    // =====================================================================

    public enum EditKind
    {
        SetControl,   // node: signal / all-way stop / two-way stop / yield
        Roundabout,   // node: replace a simple 3–4 arm junction with a ring
        OneWay,       // link: remove this direction of a street
        TurnBan,      // link: restrict the turns this lane may make
        Retime,       // node: run a timed plan instead of the AI
        AddBay,       // link: fork the approach into a left-turn bay + through lane
    }

    [Serializable] public class EditOp
    {
        public EditKind kind;
        public int node = -1;                              // SetControl, Roundabout, Retime
        public int link = -1;                              // OneWay, TurnBan, AddBay
        public ControlType control = ControlType.Signalized; // SetControl
        public int majorAxis = 0;                          // TwoWayStop/YieldEntry: 0 = E-W keeps priority, 1 = N-S
        public TurnMask turns = TurnMask.All;              // TurnBan
        public float cycle = 60f;                          // Retime
        public List<float> splits;                         // Retime: fraction per phase

        public EditOp Clone()
        {
            var c = (EditOp)MemberwiseClone();
            c.splits = splits == null ? null : new List<float>(splits);
            return c;
        }

        public bool SameAs(EditOp o)
        {
            if (o == null || kind != o.kind || node != o.node || link != o.link) return false;
            if (control != o.control || majorAxis != o.majorAxis || turns != o.turns) return false;
            if (Math.Abs(cycle - o.cycle) > 1e-3f) return false;
            int a = splits?.Count ?? 0, b = o.splits?.Count ?? 0;
            if (a != b) return false;
            for (int i = 0; i < a; i++) if (Math.Abs(splits[i] - o.splits[i]) > 1e-3f) return false;
            return true;
        }

        /// <summary>Two ops that cannot coexist: same node-level or link-level slot.
        /// Adding one replaces the other. A roundabout removes the node, so it
        /// also displaces every other op on that node.</summary>
        public bool Conflicts(EditOp o)
        {
            if (kind == EditKind.Roundabout || o.kind == EditKind.Roundabout)
                return node == o.node && node >= 0;
            if (kind != o.kind) return false;
            return kind switch
            {
                EditKind.SetControl => node == o.node,
                EditKind.Retime => node == o.node,
                EditKind.OneWay => link == o.link,
                EditKind.TurnBan => link == o.link,
                EditKind.AddBay => link == o.link,
                _ => false
            };
        }
    }

    /// <summary>A priced tool in a puzzle's toolbox. For SetControl the control
    /// type is part of the tool identity (a signal costs more than a stop sign).</summary>
    [Serializable] public class ToolDef
    {
        public EditKind kind;
        public ControlType control = ControlType.Signalized;
        public int price;
        public string label = "";
        public string blurb = "";

        public bool Matches(EditOp op)
            => op.kind == kind && (kind != EditKind.SetControl || op.control == control);
    }

    public enum ObjectiveKind { AvgWait, MaxWait, ClearCars, NoSpillback, GateQueue }

    [Serializable] public class ObjectiveDef
    {
        public ObjectiveKind kind;
        public float value;                // threshold (seconds or cars); unused for NoSpillback

        public string Describe() => kind switch
        {
            ObjectiveKind.AvgWait => $"Average wait under {value:0.#} s",
            ObjectiveKind.MaxWait => $"Nobody waits more than {value:0.#} s",
            ObjectiveKind.ClearCars => $"Get {value:F0} cars through",
            ObjectiveKind.NoSpillback => "No queue backs into another junction",
            ObjectiveKind.GateQueue => $"Never more than {value:F0} cars backed up at an entrance",
            _ => kind.ToString()
        };

        /// <summary>Read the objective's value off a finished sim.</summary>
        public float Measure(Simulation sim) => kind switch
        {
            ObjectiveKind.AvgWait => sim.Metrics.LiveAvgWait(sim),
            ObjectiveKind.MaxWait => sim.Metrics.MaxWait,
            ObjectiveKind.ClearCars => sim.Metrics.Completed,
            ObjectiveKind.NoSpillback => sim.Metrics.SpillbackEvents,
            ObjectiveKind.GateQueue => sim.Metrics.MaxGateQueue,
            _ => 0f
        };

        /// <summary>Higher is better only for ClearCars.</summary>
        public bool HigherIsBetter => kind == ObjectiveKind.ClearCars;

        public bool Passes(float measured) => kind switch
        {
            ObjectiveKind.ClearCars => measured >= value,
            ObjectiveKind.NoSpillback => measured <= 0f,
            _ => measured <= value
        };

        public string Format(float measured) => kind switch
        {
            ObjectiveKind.ClearCars => $"{measured:F0} cars",
            ObjectiveKind.NoSpillback => measured <= 0f ? "none" : $"{measured:F0}",
            ObjectiveKind.GateQueue => $"{measured:F0} cars",
            _ => $"{measured:F0} s"
        };
    }

    [Serializable] public class PuzzleDef
    {
        public string id = "";
        public string title = "";
        public string intro = "";
        public string hint = "";
        public string tutorial = "";          // shown in the build phase until the first change (first puzzles only)
        public LevelDef level;
        public List<EditOp> initialOps = new List<EditOp>();   // pre-placed; removing one is free
        public List<ToolDef> toolbox = new List<ToolDef>();
        public int budget;
        public int par;
        public List<int> seeds = new List<int> { 1, 2, 3 };
        public List<ObjectiveDef> objectives = new List<ObjectiveDef>();
        public float aiDecisionInterval = 5f;
        /// <summary>The authored answer (full op list, not a diff). Tests verify it
        /// solves under the current sim; the game can reveal it.</summary>
        public List<EditOp> answer = new List<EditOp>();

        public ToolDef ToolFor(EditOp op)
        {
            foreach (var t in toolbox) if (t.Matches(op)) return t;
            return null;
        }
    }

    [Serializable] public class WorldDef
    {
        public string id = "";
        public string title = "";
        public string blurb = "";
        public int unlockStars = 0;           // total stars needed (across earlier worlds) to open this world
        public List<PuzzleDef> puzzles = new List<PuzzleDef>();
    }

    public static class Junctions
    {
        /// <summary>A junction a player can edit: not a map edge, not a lane fork
        /// (one in-link), not a roundabout entry. Two in and two out is enough:
        /// a crossing of one-way streets is a real junction.</summary>
        public static bool IsEditable(Node n)
            => !n.IsBoundary && !(n.Control is YieldEntryControl) && n.InLinks.Count >= 2 && n.OutLinks.Count >= 2;

        public static bool IsEditable(NetworkDef net, NodeDef nd)
        {
            if (nd.isBoundary || nd.control == ControlType.YieldEntry) return false;
            int ins = 0, outs = 0;
            foreach (var l in net.links) { if (l.to == nd.id) ins++; if (l.from == nd.id) outs++; }
            return ins >= 2 && outs >= 2;
        }
    }

    public class PuzzleException : Exception
    {
        public PuzzleException(string msg) : base(msg) { }
    }

    /// <summary>The player's working answer: an ordered op list with slot
    /// semantics (one control per node, one edit per link) and a price.</summary>
    public sealed class Solution
    {
        public readonly List<EditOp> Ops = new List<EditOp>();

        public static Solution From(IEnumerable<EditOp> ops)
        {
            var s = new Solution();
            foreach (var op in ops) s.Ops.Add(op.Clone());
            return s;
        }

        /// <summary>Add an op, displacing anything it conflicts with. Returns the displaced ops.</summary>
        public List<EditOp> Add(EditOp op)
        {
            var displaced = Ops.FindAll(o => o.Conflicts(op));
            Ops.RemoveAll(o => o.Conflicts(op));
            Ops.Add(op.Clone());
            return displaced;
        }

        public bool Remove(EditOp op) => Ops.RemoveAll(o => o.SameAs(op)) > 0;

        public EditOp Find(Predicate<EditOp> pred) => Ops.Find(pred);

        /// <summary>Money spent: every op that is not one of the puzzle's
        /// pre-placed ops, priced by its tool. Throws if an op has no tool.</summary>
        public int Cost(PuzzleDef puzzle)
        {
            int cost = 0;
            foreach (var op in Ops)
            {
                if (puzzle.initialOps.Exists(i => i.SameAs(op))) continue;
                var tool = puzzle.ToolFor(op)
                           ?? throw new PuzzleException($"{op.kind} is not in this puzzle's toolbox");
                cost += tool.price;
            }
            return cost;
        }
    }

    // =====================================================================
    //  Applying ops to a LevelDef. Pure: returns a new level, never mutates
    //  the authored one. Validates that every demand flow still has a route
    //  (a banned turn or one-way that strands a flow would otherwise make
    //  that traffic silently vanish, which is a free win).
    // =====================================================================
    public static class Edits
    {
        public const float RoundaboutRadius = 15f;
        public const float RoundaboutSpeed = 8f;
        public const float BayLength = 60f;

        public static LevelDef Apply(LevelDef baseLevel, IEnumerable<EditOp> ops)
        {
            var lv = Clone(baseLevel);
            var dirty = new HashSet<int>();
            foreach (var op in ops)
            {
                switch (op.kind)
                {
                    case EditKind.SetControl:
                    {
                        var nd = NodeOrThrow(lv, op.node);
                        if (nd.isBoundary) throw new PuzzleException("the map edge cannot be controlled");
                        if (op.control == ControlType.Uncontrolled)
                            throw new PuzzleException("a junction cannot be left uncontrolled");
                        nd.control = op.control;
                        nd.majorInLinks = null;
                        if (op.control == ControlType.TwoWayStop || op.control == ControlType.YieldEntry)
                        {
                            nd.majorInLinks = AxisInLinks(lv.network, nd, op.majorAxis);
                            if (nd.majorInLinks.Count == 0)
                                throw new PuzzleException("no street on that axis to give priority to");
                        }
                        if (op.control == ControlType.Signalized) dirty.Add(nd.id);
                        break;
                    }
                    case EditKind.Roundabout:
                        MakeRoundabout(lv.network, op.node);
                        break;
                    case EditKind.OneWay:
                    {
                        var link = LinkOrThrow(lv, op.link);
                        lv.network.links.Remove(link);
                        dirty.Add(link.from); dirty.Add(link.to);
                        break;
                    }
                    case EditKind.TurnBan:
                    {
                        var link = LinkOrThrow(lv, op.link);
                        if (op.turns == TurnMask.None) throw new PuzzleException("a lane must allow some turn");
                        link.turns = op.turns;
                        dirty.Add(link.to);
                        break;
                    }
                    case EditKind.AddBay:
                        dirty.Add(LinkOrThrow(lv, op.link).to);   // the junction, before the link is re-pointed at the fork
                        AddBay(lv.network, op.link);
                        break;
                    case EditKind.Retime:
                        NodeOrThrow(lv, op.node);   // exists; applied when policies attach
                        break;
                }
            }

            foreach (int id in dirty)
            {
                var nd = lv.network.nodes.Find(n => n.id == id);
                if (nd != null && !nd.isBoundary && nd.control == ControlType.Signalized)
                    nd.phases = PhaseGen.Auto(lv.network, id);
            }

            Validate(lv);
            return lv;
        }

        /// <summary>Every flow must still be routable, and every signal plan legal.</summary>
        public static void Validate(LevelDef lv)
        {
            var net = RoadNetwork.Build(lv.network);
            var router = new Router(net);
            foreach (var f in lv.demand.flows)
                if (router.Route(f.origin, f.dest) == null)
                    throw new PuzzleException($"traffic from {NodeName(lv, f.origin)} to {NodeName(lv, f.dest)} would have no way through");
            foreach (var nd in lv.network.nodes)
                if (nd.control == ControlType.Signalized)
                {
                    if (nd.phases == null || nd.phases.Count == 0)
                        throw new PuzzleException($"junction {nd.id} has a signal but no phases");
                    net.ValidatePhases(net.NodeById(nd.id), nd.phases);
                }
        }

        /// <summary>Attach lights' policies for a puzzle run: the game AI
        /// (AgingMaxPressurePolicy) everywhere, a fixed timed plan where a
        /// Retime op says so.</summary>
        public static void AttachPolicies(Simulation sim, IEnumerable<EditOp> ops, float aiInterval = 5f,
                                          Func<SignalController, ISignalPolicy> ai = null)
        {
            ai ??= GameAi.FactoryFor(sim);
            foreach (var node in sim.Network.Nodes)
                if (node.Control is SignalController ctl)
                { ctl.Policy = ai(ctl); ctl.DecisionInterval = aiInterval; }
            foreach (var op in ops)
            {
                if (op.kind != EditKind.Retime) continue;
                if (!sim.Network.TryNode(op.node, out var node) || !(node.Control is SignalController ctl)) continue;
                ctl.Policy = new FixedTimePolicy(op.cycle, NormalizedSplits(op.splits, ctl.Phases.Count));
                ctl.DecisionInterval = 0.5f;
            }
        }

        public static float[] NormalizedSplits(List<float> splits, int phases)
        {
            var s = new float[phases];
            float sum = 0f;
            // A split list authored for a different phase count (e.g. after a bay
            // turned a 2-phase plan into 4) means nothing; fall back to even.
            if (splits != null && splits.Count == phases)
                for (int i = 0; i < phases; i++) { s[i] = splits[i] > 0f ? splits[i] : 0f; sum += s[i]; }
            if (sum <= 0f) { for (int i = 0; i < phases; i++) s[i] = 1f / phases; return s; }
            for (int i = 0; i < phases; i++) s[i] = Math.Max(s[i] / sum, 0.05f);
            sum = 0f; foreach (var v in s) sum += v;
            for (int i = 0; i < phases; i++) s[i] /= sum;
            return s;
        }

        // ------------------------------------------------------------ helpers

        public static string NodeName(LevelDef lv, int id)
        {
            var nd = lv.network.nodes.Find(n => n.id == id);
            if (nd == null) return $"#{id}";
            if (!nd.isBoundary) return $"junction {id}";
            // Compass name from the arm's direction off the junction it hangs from.
            var link = lv.network.links.Find(l => l.from == id) ?? lv.network.links.Find(l => l.to == id);
            var from = link == null ? null : lv.network.nodes.Find(n => n.id == (link.from == id ? link.to : link.from));
            float cx = from?.x ?? 0f, cy = from?.y ?? 0f;
            float dx = nd.x - cx, dy = nd.y - cy;
            return Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? "the east" : "the west") : (dy > 0 ? "the north" : "the south");
        }

        /// <summary>"from the north": the map edge an approach comes from, walking
        /// up through forks. Falls back to the link id.</summary>
        public static string ApproachName(LevelDef lv, int linkId)
        {
            var net = lv.network;
            var link = net.links.Find(l => l.id == linkId);
            int hops = 0;
            while (link != null && hops++ < 4)
            {
                var from = net.nodes.Find(n => n.id == link.from);
                if (from == null) break;
                if (from.isBoundary) return "from " + NodeName(lv, from.id);
                if (!from.isBoundary && net.links.FindAll(l => l.to == from.id).Count >= 3) return "from " + NodeName(lv, from.id);
                link = net.links.Find(l => l.to == from.id);
            }
            return $"street {linkId}";
        }

        public static string ControlName(ControlType c, int axis = 0) => c switch
        {
            ControlType.Signalized => "Traffic signal",
            ControlType.AllWayStop => "All-way stop",
            ControlType.TwoWayStop => axis == 0 ? "Two-way stop (E-W keeps priority)" : "Two-way stop (N-S keeps priority)",
            ControlType.YieldEntry => "Yield",
            _ => "Uncontrolled"
        };

        /// <summary>One line for an op, for change lists and undo buttons.</summary>
        public static string Describe(EditOp op, LevelDef lv) => op.kind switch
        {
            EditKind.SetControl => ControlName(op.control, op.majorAxis),
            EditKind.Roundabout => "Roundabout",
            EditKind.Retime => op.splits != null && op.splits.Count == 2
                ? $"Timed plan: {op.cycle:F0} s cycle, N-S {op.splits[0] * 100f:F0}% / E-W {op.splits[1] * 100f:F0}%"
                : $"Timed plan: {op.cycle:F0} s cycle, even split",
            EditKind.AddBay => $"Left-turn bay {ApproachName(lv, op.link)}",
            EditKind.TurnBan => $"No left turn {ApproachName(lv, op.link)}",
            EditKind.OneWay => $"One-way: closed the lane {ApproachName(lv, op.link)}",
            _ => op.kind.ToString()
        };

        static NodeDef NodeOrThrow(LevelDef lv, int id)
            => lv.network.nodes.Find(n => n.id == id) ?? throw new PuzzleException($"junction {id} no longer exists");

        static LinkDef LinkOrThrow(LevelDef lv, int id)
            => lv.network.links.Find(l => l.id == id) ?? throw new PuzzleException($"street {id} no longer exists");

        /// <summary>In-links arriving along one axis: 0 = east-west, 1 = north-south.</summary>
        public static List<int> AxisInLinks(NetworkDef net, NodeDef nd, int axis)
        {
            var list = new List<int>();
            foreach (var l in net.links)
            {
                if (l.to != nd.id) continue;
                var from = net.nodes.Find(n => n.id == l.from);
                float dx = nd.x - from.x, dy = nd.y - from.y;
                bool ew = Math.Abs(dx) > Math.Abs(dy);
                if (ew == (axis == 0)) list.Add(l.id);
            }
            return list;
        }

        static void MakeRoundabout(NetworkDef net, int nodeId)
        {
            var nd = net.nodes.Find(n => n.id == nodeId) ?? throw new PuzzleException($"junction {nodeId} no longer exists");
            if (nd.isBoundary) throw new PuzzleException("the map edge cannot become a roundabout");

            // Group the node's links by neighbour; each arm must be one in + one out.
            var arms = new Dictionary<int, (LinkDef inL, LinkDef outL)>();
            foreach (var l in net.links)
            {
                if (l.to == nodeId)
                {
                    arms.TryGetValue(l.from, out var a);
                    if (a.inL != null) throw new PuzzleException("a roundabout needs simple two-way arms (this junction has extra lanes)");
                    arms[l.from] = (l, a.outL);
                }
                else if (l.from == nodeId)
                {
                    arms.TryGetValue(l.to, out var a);
                    if (a.outL != null) throw new PuzzleException("a roundabout needs simple two-way arms (this junction has extra lanes)");
                    arms[l.to] = (a.inL, l);
                }
            }
            if (arms.Count < 3 || arms.Count > 4) throw new PuzzleException("a roundabout needs 3 or 4 arms");
            foreach (var kv in arms)
            {
                if (kv.Value.inL == null || kv.Value.outL == null)
                    throw new PuzzleException("a roundabout needs two-way arms (one street here is one-way)");
                if (kv.Value.inL.length < RoundaboutRadius + 10f || kv.Value.outL.length < RoundaboutRadius + 10f)
                    throw new PuzzleException("the streets here are too short for a roundabout");
            }

            int nextNode = 0, nextLink = 0;
            foreach (var n in net.nodes) nextNode = Math.Max(nextNode, n.id + 1);
            foreach (var l in net.links) nextLink = Math.Max(nextLink, l.id + 1);

            // Sort arms counter-clockwise (y up), which is the circulating direction.
            var order = new List<(double angle, int neighbour)>();
            foreach (var kv in arms)
            {
                var nb = net.nodes.Find(n => n.id == kv.Key);
                order.Add((Math.Atan2(nb.y - nd.y, nb.x - nd.x), kv.Key));
            }
            order.Sort((a, b) => a.angle.CompareTo(b.angle));

            var merges = new List<NodeDef>();
            foreach (var (angle, neighbour) in order)
            {
                var m = new NodeDef
                {
                    id = nextNode++,
                    x = nd.x + (float)Math.Cos(angle) * RoundaboutRadius,
                    y = nd.y + (float)Math.Sin(angle) * RoundaboutRadius,
                    control = ControlType.YieldEntry,
                    gapThreshold = nd.gapThreshold,
                    majorInLinks = new List<int>(),
                };
                net.nodes.Add(m); merges.Add(m);
                var (inL, outL) = arms[neighbour];
                inL.to = m.id; inL.length -= RoundaboutRadius;
                outL.from = m.id; outL.length -= RoundaboutRadius;
            }
            for (int i = 0; i < merges.Count; i++)
            {
                int j = (i + 1) % merges.Count;
                double da = order[j].angle - order[i].angle;
                if (da <= 0) da += 2 * Math.PI;
                var arc = new LinkDef
                {
                    id = nextLink++, from = merges[i].id, to = merges[j].id,
                    length = (float)(da * RoundaboutRadius), speedLimit = RoundaboutSpeed,
                };
                net.links.Add(arc);
                merges[j].majorInLinks.Add(arc.id);   // entries yield to the circulating arc
            }
            net.nodes.Remove(nd);
        }

        static void AddBay(NetworkDef net, int inLinkId)
        {
            var link = net.links.Find(l => l.id == inLinkId) ?? throw new PuzzleException($"street {inLinkId} no longer exists");
            var nd = net.nodes.Find(n => n.id == link.to);
            var from = net.nodes.Find(n => n.id == link.from);
            if (nd.isBoundary) throw new PuzzleException("a bay must lead into a junction");
            if (link.turns != TurnMask.All) throw new PuzzleException("this lane already has a turn restriction");
            foreach (var l in net.links)
                if (l != link && l.from == link.from && l.to == link.to)
                    throw new PuzzleException("this approach already has extra lanes");
            if (link.length < BayLength + 30f) throw new PuzzleException("this approach is too short for a turn bay");

            int nextNode = 0, nextLink = 0;
            foreach (var n in net.nodes) nextNode = Math.Max(nextNode, n.id + 1);
            foreach (var l in net.links) nextLink = Math.Max(nextLink, l.id + 1);

            float dx = nd.x - from.x, dy = nd.y - from.y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float ux = dx / len, uy = dy / len;
            // Geometry is cosmetic; place the fork the same fraction along the drawn arm as along the modelled length.
            float frac = BayLength / link.length;
            var fork = new NodeDef { id = nextNode, x = nd.x - dx * frac, y = nd.y - dy * frac };
            net.nodes.Add(fork);

            link.to = fork.id; link.length -= BayLength;
            net.links.Add(new LinkDef { id = nextLink, from = fork.id, to = nd.id, length = BayLength, speedLimit = link.speedLimit, turns = TurnMask.Left });
            net.links.Add(new LinkDef { id = nextLink + 1, from = fork.id, to = nd.id, length = BayLength, speedLimit = link.speedLimit, turns = TurnMask.Through | TurnMask.Right });
        }

        public static LevelDef Clone(LevelDef s)
        {
            var d = new LevelDef { name = s.name, duration = s.duration };
            foreach (var n in s.network.nodes)
            {
                var c = new NodeDef
                {
                    id = n.id, x = n.x, y = n.y, isBoundary = n.isBoundary, control = n.control,
                    minGreen = n.minGreen, yellow = n.yellow, allRed = n.allRed,
                    stopServiceTime = n.stopServiceTime, gapThreshold = n.gapThreshold,
                    majorInLinks = n.majorInLinks == null ? null : new List<int>(n.majorInLinks),
                };
                if (n.phases != null)
                {
                    c.phases = new List<PhaseDef>();
                    foreach (var p in n.phases)
                        c.phases.Add(new PhaseDef { movements = new List<int>(p.movements), permissive = new List<int>(p.permissive) });
                }
                d.network.nodes.Add(c);
            }
            foreach (var l in s.network.links)
                d.network.links.Add(new LinkDef { id = l.id, from = l.from, to = l.to, length = l.length, speedLimit = l.speedLimit, turns = l.turns });
            foreach (var f in s.demand.flows)
                d.demand.flows.Add(new OdFlowDef
                {
                    origin = f.origin, dest = f.dest,
                    rate = new RateCurve { times = new List<float>(f.rate.times), rates = new List<float>(f.rate.rates) }
                });
            return d;
        }
    }

    // =====================================================================
    //  Phase generation from geometry. Casual players never author phases:
    //  approaches group by axis (N-S, E-W); an axis whose approach has a
    //  left-only lane (a bay) gets a protected left phase, otherwise lefts
    //  run permissive. Generalizes NetworkBuilder.TwoPhasePermissiveLefts and
    //  LaneBuilder.BuildPhases to any node position and any arm count.
    // =====================================================================
    public static class PhaseGen
    {
        public static List<PhaseDef> Auto(NetworkDef def, int nodeId)
        {
            var net = RoadNetwork.Build(def);
            var node = net.NodeById(nodeId);

            bool IsNs(Movement m)
            {
                var inL = net.LinkById(m.InLink);
                var from = net.NodeById(inL.From);
                return Math.Abs(node.Y - from.Y) > Math.Abs(node.X - from.X);
            }
            bool AxisHasBay(bool ns)
            {
                foreach (int inId in node.InLinks)
                {
                    var l = net.LinkById(inId);
                    var from = net.NodeById(l.From);
                    bool isNs = Math.Abs(node.Y - from.Y) > Math.Abs(node.X - from.X);
                    if (isNs == ns && l.Turns == TurnMask.Left) return true;
                }
                return false;
            }

            var phases = new List<PhaseDef>();
            for (int axis = 0; axis < 2; axis++)
            {
                bool ns = axis == 0;
                var mv = new List<Movement>();
                foreach (var m in node.Movements) if (IsNs(m) == ns) mv.Add(m);
                if (mv.Count == 0) continue;

                if (AxisHasBay(ns))
                {
                    var thr = new PhaseDef(); var left = new PhaseDef();
                    foreach (var m in mv)
                        (m.Turn == TurnMask.Left ? left : thr).movements.Add(m.Index);
                    if (thr.movements.Count > 0) phases.Add(thr);
                    if (left.movements.Count > 0) phases.Add(left);
                }
                else
                {
                    var p = new PhaseDef();
                    foreach (var m in mv)
                    {
                        p.movements.Add(m.Index);
                        if (m.Turn != TurnMask.Left) continue;
                        foreach (var other in mv)
                            if (other.Index != m.Index && node.Conflicts[m.Index, other.Index])
                            { p.permissive.Add(m.Index); break; }
                    }
                    phases.Add(p);
                }
            }
            if (phases.Count == 0) throw new PuzzleException($"junction {nodeId} has no movements to signal");
            net.ValidatePhases(node, phases);
            return phases;
        }
    }
}
