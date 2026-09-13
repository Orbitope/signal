using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// SCHEMA V2 — the canonical observation/action/reward definition for RL.
    /// This class is the single source of truth; the env server, any future
    /// in-game learned policy, and all documentation derive from here.
    ///
    /// Per-agent observation: 105 floats.
    ///   [0..23]   own approaches ×4, canonical N,E,S,W by bearing, 6 floats each:
    ///             (bayQueue/20, bayHeadWait/120, thrQueue/20, thrHeadWait/120,
    ///              hasBay, valid)
    ///             thrQueue SUMS all through lanes (interchangeable => lossless);
    ///             thrHeadWait is the MAX head wait across them. Approaches
    ///             whose in-links start at a boundary FOLD the entry-gate held
    ///             count into thrQueue — otherwise saturation beyond the gate
    ///             is invisible to the agent while the benchmark counts it.
    ///             Missing approach: (-1,-1,-1,-1,0,0).
    ///   [24..31]  current phase one-hot, MAX_PHASES=8.
    ///   [32]      timeInPhase / minGreen, capped at 2.
    ///   [33..104] neighbors ×4 (upstream node per approach slot), 18 floats:
    ///             (their 4 approach agg queues /20 (missing=-1), phase one-hot 8,
    ///              connecting-link occupancy 1, control one-hot 4
    ///              [signal|stop|yield|none], valid 1). Missing neighbor: zeros.
    ///
    /// Actions: Discrete(8) = requested phase. Mask forbids p >= phase count and
    /// any non-current phase while timeInPhase &lt; minGreen.
    ///
    /// Reward (per sim tick, accumulate between decisions):
    ///   -(Σ own approach queues incl. gate-held  -  0.5 · Σ out-link queues) · DT
    /// scaled by RewardScale at decision time. Locally computable pressure;
    /// punishes flushing into a full downstream link (the MaxPressure insight).
    /// </summary>
    public static class ObsSchema
    {
        // SCHEMA V3: 8 octant approach slots (N,NE,E,SE,S,SW,W,NW) so irregular /
        // diagonal intersections with 5–6 legs are representable. A plain 4-leg node
        // uses only the cardinal slots (0,2,4,6); the rest read missing. Regular
        // levels are unchanged in meaning, just wider (more zero slots).
        public const int MaxApproaches = 8;
        public const int MaxPhases = 8;
        public const int MaxNeighbors = 8;
        public const int ApproachFloats = 6;
        public const int NeighborFloats = MaxApproaches + MaxPhases + 1 + 4 + 1;   // 22
        // Self block = own approaches + phase one-hot + timeInPhase. Neighbours follow.
        public const int SelfFloats = MaxApproaches * ApproachFloats + MaxPhases + 1;
        public const int Size = SelfFloats + MaxNeighbors * NeighborFloats;         // 233
        public const float QueueNorm = 20f, WaitNorm = 120f;
        public const float RewardScale = 0.01f;
        // The cardinal slots, for consumers that read N/E/S/W metrics.
        public static readonly int[] Cardinal = { 0, 2, 4, 6 };
    }

    /// <summary>Per-node cached wiring + obs/mask/reward computation for one agent.</summary>
    public sealed class AgentView
    {
        public readonly Node Node;
        public readonly SignalController Ctl;
        public readonly ExternalPolicy Policy;

        // Per canonical slot (N,E,S,W): lane-links grouped by role, plus the
        // upstream arm feeder (boundary/arm -> fork) and gate origin if any.
        private readonly int[][] _bayLinks = new int[ObsSchema.MaxApproaches][];
        private readonly int[][] _thrLinks = new int[ObsSchema.MaxApproaches][];
        private readonly int[] _armLink = new int[ObsSchema.MaxApproaches];        // feeder link id or -1
        private readonly int[] _gateOrigin = new int[ObsSchema.MaxApproaches];     // boundary node id or -1
        private readonly Node[] _neighbor = new Node[ObsSchema.MaxNeighbors];
        private readonly Link[] _neighborConn = new Link[ObsSchema.MaxNeighbors];

        /// <param name="attach">True (trainer): take over the controller with an
        /// ExternalPolicy driven from outside. False (in-engine policy): only
        /// build the observation view; the controller keeps its own policy.</param>
        public AgentView(Simulation sim, Node node, bool attach = true)
        {
            Node = node;
            Ctl = (SignalController)node.Control;
            if (attach)
            {
                Policy = new ExternalPolicy();
                Ctl.Policy = Policy;
                Ctl.DecisionInterval = float.MaxValue;   // the trainer drives decisions
            }

            // Group in-links into canonical approach slots. An approach may be a
            // direct arm (single-lane) or a fork bundle (lanes). We classify by
            // the bearing of the ORIGIN of the approach: the fork's upstream arm
            // if one exists, else the in-link itself.
            var bays = new List<int>[ObsSchema.MaxApproaches];
            var thrs = new List<int>[ObsSchema.MaxApproaches];
            for (int s = 0; s < ObsSchema.MaxApproaches; s++)
            { bays[s] = new List<int>(); thrs[s] = new List<int>(); _gateOrigin[s] = -1; _armLink[s] = -1; }

            foreach (int inId in node.InLinks)
            {
                var inL = sim.Network.LinkById(inId);
                var from = sim.Network.NodeById(inL.From);
                // Walk one hop upstream through a pure fork to find the arm origin
                // and the feeder link.
                Node origin = from;
                int arm = -1;
                if (!from.IsBoundary && from.InLinks.Count == 1 && from.Control is UncontrolledControl)
                {
                    arm = from.InLinks[0];
                    origin = sim.Network.NodeById(sim.Network.LinkById(arm).From);
                }

                int slot = SlotFor(origin.X - Node.X, origin.Y - Node.Y);
                if (inL.Turns == TurnMask.Left) bays[slot].Add(inId);
                else thrs[slot].Add(inId);

                if (arm >= 0) _armLink[slot] = arm;
                if (origin.IsBoundary) _gateOrigin[slot] = origin.Id;
                _neighborFromSlot(sim, slot, from, inL);
            }
            for (int s = 0; s < ObsSchema.MaxApproaches; s++)
            { _bayLinks[s] = bays[s].ToArray(); _thrLinks[s] = thrs[s].ToArray(); }
        }

        private void _neighborFromSlot(Simulation sim, int slot, Node from, Link inL)
        {
            // Neighbor = the upstream INTERSECTION on this slot (skip forks).
            Node cand = from; Link conn = inL;
            if (!from.IsBoundary && from.InLinks.Count == 1 && from.Control is UncontrolledControl)
            {
                conn = sim.Network.LinkById(from.InLinks[0]);
                cand = sim.Network.NodeById(conn.From);
            }
            if (!cand.IsBoundary) { _neighbor[slot] = cand; _neighborConn[slot] = conn; }
        }

        // Octant of the approach's upstream bearing: 0=N,1=NE,2=E,3=SE,4=S,5=SW,
        // 6=W,7=NW. Cardinal-only nodes land on the even slots exactly as before.
        private static int SlotFor(float dx, float dy)
        {
            double bearing = Math.Atan2(dx, dy) * 180.0 / Math.PI;   // 0=N, 90=E
            int oct = ((int)Math.Round(bearing / 45.0) % 8 + 8) % 8;
            return oct;
        }

        // -----------------------------------------------------------------

        public void WriteObs(Simulation sim, float[] dst, int offset)
        {
            int i = offset;

            for (int s = 0; s < ObsSchema.MaxApproaches; s++)
            {
                bool valid = _bayLinks[s].Length + _thrLinks[s].Length > 0;
                if (!valid)
                { dst[i++] = -1; dst[i++] = -1; dst[i++] = -1; dst[i++] = -1; dst[i++] = 0; dst[i++] = 0; continue; }

                (float q, float w) Agg(int[] links)
                {
                    float q = 0, w = 0;
                    foreach (int id in links)
                    {
                        var l = sim.Network.LinkById(id);
                        q += l.QueueCount();
                        var f = l.Front;
                        if (f != null && f.Wait > w) w = f.Wait;
                    }
                    return (q, w);
                }
                var (bq, bw) = Agg(_bayLinks[s]);
                var (tq, tw) = Agg(_thrLinks[s]);

                // Fold upstream demand into the correct role by ROUTE: vehicles
                // on the arm feeder and held at the spawn gate already know
                // which lane they'll take — count a future left-turner toward
                // the bay, not the through queue.
                void FoldByRoute(Vehicle veh)
                {
                    if (RouteTakesBay(sim, veh)) bq += 1; else tq += 1;
                }
                if (_armLink[s] >= 0)
                {
                    var arm = sim.Network.LinkById(_armLink[s]);
                    for (int vi = 0; vi < arm.Vehicles.Count; vi++)
                        if (arm.Vehicles[vi].Speed < SimConfig.QueueSpeed)
                            FoldByRoute(arm.Vehicles[vi]);
                }
                if (_gateOrigin[s] >= 0 &&
                    sim.Demand.EntryQueues.TryGetValue(_gateOrigin[s], out var gate))
                    foreach (var veh in gate) FoldByRoute(veh);

                bool hasBay = _bayLinks[s].Length > 0;
                dst[i++] = hasBay ? Clamp01(bq / ObsSchema.QueueNorm) : -1f;
                dst[i++] = hasBay ? Clamp01(bw / ObsSchema.WaitNorm) : -1f;
                dst[i++] = Clamp01(tq / ObsSchema.QueueNorm);
                dst[i++] = Clamp01(tw / ObsSchema.WaitNorm);
                dst[i++] = hasBay ? 1f : 0f;
                dst[i++] = 1f;
            }

            for (int p = 0; p < ObsSchema.MaxPhases; p++)
                dst[i++] = p == Ctl.CurrentPhase ? 1f : 0f;
            dst[i++] = Math.Min(Ctl.TimeInPhase / Ctl.MinGreen, 2f);

            for (int s = 0; s < ObsSchema.MaxNeighbors; s++)
            {
                var nb = _neighbor[s];
                if (nb == null) { for (int k = 0; k < ObsSchema.NeighborFloats; k++) dst[i++] = 0f; continue; }

                // Their aggregate approach queues in THEIR canonical slots.
                for (int a = 0; a < ObsSchema.MaxApproaches; a++) _nbq[a] = -1f;
                foreach (int inId in nb.InLinks)
                {
                    var l = sim.Network.LinkById(inId);
                    var from = sim.Network.NodeById(l.From);
                    int slot = SlotFor(from.X - nb.X, from.Y - nb.Y);
                    if (_nbq[slot] < 0) _nbq[slot] = 0;
                    _nbq[slot] += l.QueueCount();
                }
                for (int a = 0; a < ObsSchema.MaxApproaches; a++)
                    dst[i++] = _nbq[a] < 0 ? -1f : Clamp01(_nbq[a] / ObsSchema.QueueNorm);

                var nbCtl = nb.Control as SignalController;
                for (int p = 0; p < ObsSchema.MaxPhases; p++)
                    dst[i++] = nbCtl != null && p == nbCtl.CurrentPhase ? 1f : 0f;
                var conn = _neighborConn[s];
                dst[i++] = Clamp01(conn.OccupiedLength() / conn.Length);
                dst[i++] = nbCtl != null ? 1f : 0f;
                dst[i++] = nb.Control is AllWayStopControl ? 1f : 0f;
                dst[i++] = nb.Control is YieldEntryControl || nb.Control is TwoWayStopControl ? 1f : 0f;
                dst[i++] = nb.Control is UncontrolledControl ? 1f : 0f;
                dst[i++] = 1f;
            }
        }
        private readonly float[] _nbq = new float[ObsSchema.MaxApproaches];

        private bool RouteTakesBay(Simulation sim, Vehicle veh)
        {
            // Find the first lane-link of this approach on the vehicle's route.
            for (int r = veh.RouteIdx; r < veh.Route.Length && r < veh.RouteIdx + 3; r++)
            {
                var l = sim.Network.LinkById(veh.Route[r]);
                if (l.To == Node.Id) return l.Turns == TurnMask.Left;
            }
            return false;
        }

        /// <summary>mask[p]=1 iff requesting phase p is legal now.</summary>
        public void WriteMask(byte[] dst, int offset)
        {
            for (int p = 0; p < ObsSchema.MaxPhases; p++)
            {
                bool legal = p < Ctl.Phases.Count &&
                             (p == Ctl.CurrentPhase || Ctl.TimeInPhase >= Ctl.MinGreen - 0.051f);
                dst[offset + p] = legal ? (byte)1 : (byte)0;
            }
        }

        /// <summary>One tick of pressure (call every sim step between decisions).</summary>
        public float TickPressure(Simulation sim)
        {
            float pressure = 0f;
            for (int s = 0; s < ObsSchema.MaxApproaches; s++)
            {
                foreach (int id in _bayLinks[s]) pressure += sim.Network.LinkById(id).QueueCount();
                foreach (int id in _thrLinks[s]) pressure += sim.Network.LinkById(id).QueueCount();
                if (_armLink[s] >= 0)
                    pressure += sim.Network.LinkById(_armLink[s]).QueueCount();
                if (_gateOrigin[s] >= 0 &&
                    sim.Demand.EntryQueues.TryGetValue(_gateOrigin[s], out var gate))
                    pressure += gate.Count;                 // arm + gate folding, reward side
            }
            foreach (int outId in Node.OutLinks)
                pressure -= sim.Network.LinkById(outId).QueueCount() * 0.5f;
            return -pressure * SimConfig.DT;
        }

        public void Act(int phase) { Policy.Request(phase); Ctl.RequestPhase(phase); }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
