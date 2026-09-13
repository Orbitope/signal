using System;
using System.Collections.Generic;

namespace Signal.Core
{
    public sealed class Metrics
    {
        public int Completed;
        public float CompletedWaitSum, CompletedTravelSum;
        public float MaxWait;                 // worst individual wait seen (starvation detector)
        public int SpillbackEvents;

        /// <summary>Average wait over EVERY vehicle that has ever existed —
        /// completed, in-system, and held at entry gates. Counting only
        /// completed vehicles makes starving an approach look free; this is
        /// the metric the reward, the score, and the benchmark all share.</summary>
        public float LiveAvgWait(Simulation sim)
        {
            float sum = CompletedWaitSum; int n = Completed;
            foreach (var link in sim.Network.Links)
                for (int i = 0; i < link.Vehicles.Count; i++)
                { sum += link.Vehicles[i].Wait; n++; }
            sim.Demand.AccumulateHeldWait(ref sum, ref n);
            return n > 0 ? sum / n : 0f;
        }

        public float ThroughputPerMin(Simulation sim)
            => sim.Time > 1f ? Completed / (sim.Time / 60f) : 0f;
    }

    public sealed class Simulation
    {
        public readonly RoadNetwork Network;
        public readonly DemandSource Demand;
        public readonly Router Router;
        public readonly Metrics Metrics = new Metrics();
        public float Time { get; private set; }
        public long StepCount { get; private set; }

        // Events for the presentation layer (never used by Core logic).
        public event Action<Vehicle> VehicleSpawned;
        public event Action<Vehicle> VehicleDespawned;
        public event Action<int> SpillbackStarted;    // link id

        private readonly bool[] _spillbackActive;      // per link, edge-triggered event

        public Simulation(LevelDef level, ulong seed, BuildVariant build = null)
        {
            Network = RoadNetwork.Build(level.network);
            Router = new Router(Network);
            Demand = new DemandSource(level.demand, Router, new Rng(seed));
            _spillbackActive = new bool[MaxLinkId() + 1];
            AttachControls(level.network, build);
        }

        private int MaxLinkId()
        {
            int max = 0; foreach (var l in Network.Links) max = Math.Max(max, l.Id); return max;
        }

        private void AttachControls(NetworkDef def, BuildVariant build)
        {
            foreach (var nd in def.nodes)
            {
                var node = Network.NodeById(nd.id);
                var control = nd.control;
                if (build != null)
                {
                    int bi = build.nodeIds.IndexOf(nd.id);
                    if (bi >= 0) control = build.controls[bi];
                }
                switch (control)
                {
                    case ControlType.Signalized:
                        if (nd.phases == null || nd.phases.Count == 0)
                            throw new InvalidOperationException($"Node {nd.id} is Signalized but has no phases.");
                        Network.ValidatePhases(node, nd.phases);
                        node.Control = new SignalController(node, nd.phases, nd.minGreen, nd.yellow, nd.allRed, nd.gapThreshold);
                        break;
                    case ControlType.AllWayStop:
                        node.Control = new AllWayStopControl(nd.stopServiceTime);
                        break;
                    case ControlType.TwoWayStop:
                        node.Control = new TwoWayStopControl(nd.majorInLinks, nd.gapThreshold);
                        break;
                    case ControlType.YieldEntry:
                        node.Control = new YieldEntryControl(nd.majorInLinks, nd.gapThreshold);
                        break;
                    default:
                        node.Control = UncontrolledControl.Instance;
                        break;
                }
            }
        }

        public SignalController ControllerAt(int nodeId) => Network.NodeById(nodeId).Control as SignalController;

        internal void OnVehicleSpawned(Vehicle v) => VehicleSpawned?.Invoke(v);

        // =================================================================
        //  The step. Fixed order, fixed DT — this ordering IS the contract
        //  that makes runs reproducible. Do not reorder casually.
        // =================================================================
        public void Step()
        {
            const float dt = SimConfig.DT;
            Time += dt; StepCount++;

            // 1) Demand: sample arrivals, release entry queues.
            Demand.Tick(this, dt);

            // 2) Controls: advance state machines; policies decide.
            foreach (var node in Network.Nodes)
                node.Control?.Tick(this, node, dt);

            // 3) Longitudinal dynamics per link (front-first order).
            foreach (var link in Network.Links)
                StepLink(link, dt);

            // 4) Transfers: front vehicles crossing stop lines.
            foreach (var link in Network.Links)
                TryTransfer(link);

            // 5) Bookkeeping: wait accrual, stop flags.
            foreach (var link in Network.Links)
            {
                float stopLineDist = link.Length;
                for (int i = 0; i < link.Vehicles.Count; i++)
                {
                    var v = link.Vehicles[i];
                    if (v.Speed < SimConfig.WaitSpeed) v.Wait += dt;
                    if (v.Wait > Metrics.MaxWait) Metrics.MaxWait = v.Wait;
                    if (!v.HasStopped && v.Speed < 0.15f && (stopLineDist - v.Pos) < 3f && i == 0)
                        v.HasStopped = true;
                }
            }
        }

        private void StepLink(Link link, float dt)
        {
            var vehicles = link.Vehicles;
            if (vehicles.Count == 0) return;

            for (int i = 0; i < vehicles.Count; i++)
            {
                var v = vehicles[i];
                float gap, closing;

                if (i > 0)
                {
                    var leader = vehicles[i - 1];
                    gap = (leader.Pos - leader.Length) - v.Pos;
                    closing = v.Speed - leader.Speed;
                }
                else
                {
                    // Front vehicle: obstacle is the stop line, unless it may
                    // proceed — then look through the node into the next link.
                    ResolveFront(link, v, out gap, out closing);
                }

                float a = Idm.Acceleration(v.Speed, link.SpeedLimit, gap, closing);
                v.Speed = Math.Max(0f, v.Speed + a * dt);
            }
            // Integrate positions after all accelerations (synchronous update).
            for (int i = 0; i < vehicles.Count; i++)
                vehicles[i].Pos += vehicles[i].Speed * dt;
        }

        /// <summary>Lane choice at forks: among parallel lane-links to the same
        /// downstream node whose masks permit this vehicle's subsequent turn,
        /// pick the least occupied. The only place routes bend — drivers choose
        /// a lane entering a road, never mid-link. Deterministic (ties: lowest id).</summary>
        private void ChooseLane(Node node, Link fromLink, Vehicle v)
        {
            if (node.OutLinks.Count < 2) return;
            var planned = Network.LinkById(v.NextLink);
            int follow = v.RouteIdx + 2 < v.Route.Length ? v.Route[v.RouteIdx + 2] : -1;
            var best = planned;
            float bestOcc = planned.OccupiedLength();
            foreach (int candId in node.OutLinks)
            {
                if (candId == planned.Id) continue;
                var cand = Network.LinkById(candId);
                if (cand.To != planned.To) continue;
                if (node.FindMovement(fromLink.Id, candId) == null) continue;
                if (follow >= 0 && Network.NodeById(cand.To).FindMovement(candId, follow) == null) continue;
                float occ = cand.OccupiedLength();
                if (occ < bestOcc - 0.01f || (Math.Abs(occ - bestOcc) <= 0.01f && candId < best.Id))
                { best = cand; bestOcc = occ; }
            }
            if (best.Id != planned.Id) v.Route[v.RouteIdx + 1] = best.Id;
        }

        private void ResolveFront(Link link, Vehicle v, out float gap, out float closing)
        {
            float toLine = link.Length - v.Pos;
            var node = Network.NodeById(link.To);

            if (v.OnLastLink || node.IsBoundary)
            {
                gap = float.MaxValue; closing = 0f;   // free exit at sink
                return;
            }

            var movement = node.FindMovement(link.Id, v.NextLink);
            bool allowed = movement != null && node.Control.MayEnter(this, node, v, movement);

            if (!allowed)
            {
                gap = toLine; closing = v.Speed;      // brake for the line
                return;
            }

            ChooseLane(node, link, v);
            if (movement.OutLink != v.NextLink)
                movement = node.FindMovement(link.Id, v.NextLink);
            var next = Network.LinkById(v.NextLink);

            // True spillback: the downstream link is essentially full. Hold at
            // the line and fire the (edge-triggered) event.
            bool linkFull = next.OccupiedLength() + v.Length + SimConfig.JamGap > next.Length;
            if (linkFull)
            {
                gap = toLine; closing = v.Speed;
                if (!_spillbackActive[link.Id] && v.Speed < 0.5f && toLine < 4f)
                {
                    _spillbackActive[link.Id] = true;
                    Metrics.SpillbackEvents++;
                    SpillbackStarted?.Invoke(link.Id);
                }
                return;
            }
            _spillbackActive[link.Id] = false;

            // Continuity: follow the rear vehicle of the next link through the
            // node. IDM's own spacing keeps us off its bumper — a leader still
            // near the downstream entry is close-following, not spillback.
            var tail = next.Vehicles.Count > 0 ? next.Vehicles[next.Vehicles.Count - 1] : null;
            if (tail != null)
            {
                gap = toLine + (tail.Pos - tail.Length);
                closing = v.Speed - tail.Speed;
            }
            else { gap = float.MaxValue; closing = 0f; }
        }

        private void TryTransfer(Link link)
        {
            // Only the front vehicle can cross; loop in case of multiple crossings
            // in one tick on very short links.
            while (link.Vehicles.Count > 0)
            {
                var v = link.Vehicles[0];
                if (v.Pos < link.Length) break;

                var node = Network.NodeById(link.To);

                if (v.OnLastLink || node.IsBoundary)
                {
                    link.Vehicles.RemoveAt(0);
                    Metrics.Completed++;
                    Metrics.CompletedWaitSum += v.Wait;
                    Metrics.CompletedTravelSum += Time - v.SpawnTime;
                    VehicleDespawned?.Invoke(v);
                    continue;
                }

                var movement = node.FindMovement(link.Id, v.NextLink);
                bool allowed = movement != null && node.Control.MayEnter(this, node, v, movement);
                var next = Network.LinkById(v.NextLink);

                if (allowed)
                {
                    ChooseLane(node, link, v);
                    if (v.NextLink != next.Id)
                    {
                        next = Network.LinkById(v.NextLink);
                        movement = node.FindMovement(link.Id, v.NextLink);
                        allowed = node.Control.MayEnter(this, node, v, movement);
                    }
                }

                if (allowed && next.HasEntrySpace(v.Length))
                {
                    link.Vehicles.RemoveAt(0);
                    node.Control.OnVehicleEntered(this, node, v, movement);
                    v.Pos -= link.Length;              // carry overshoot
                    v.RouteIdx++;
                    v.HasStopped = false;
                    next.Vehicles.Add(v);              // enters at the rear
                }
                else
                {
                    // Braking should prevent this; clamp defensively.
                    v.Pos = link.Length - 0.01f;
                    v.Speed = 0f;
                    break;
                }
            }
        }

        // =================================================================
        //  Determinism hash — FNV-1a over quantized state. Two runs with the
        //  same seed and same external inputs must produce identical hashes
        //  forever. This is the CI regression gate.
        // =================================================================
        public ulong StateHash()
        {
            ulong h = 14695981039346656037UL;
            void Mix(ulong x) { h ^= x; h *= 1099511628211UL; }
            Mix((ulong)StepCount);
            foreach (var link in Network.Links)
            {
                Mix((ulong)link.Id);
                for (int i = 0; i < link.Vehicles.Count; i++)
                {
                    var v = link.Vehicles[i];
                    Mix((ulong)v.Id);
                    Mix((ulong)(long)Math.Round(v.Pos * 100.0));
                    Mix((ulong)(long)Math.Round(v.Speed * 100.0));
                }
            }
            foreach (var node in Network.Nodes)
            {
                if (node.Control is SignalController s)
                {
                    Mix((ulong)node.Id);
                    Mix((ulong)s.CurrentPhase);
                    Mix((ulong)s.State);
                }
            }
            return h;
        }

        public int VehiclesInSystem()
        {
            int n = Demand.HeldCount();
            foreach (var l in Network.Links) n += l.Vehicles.Count;
            return n;
        }
    }
}
