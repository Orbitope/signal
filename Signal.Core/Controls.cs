using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// An intersection control answers one question for the simulation:
    /// may this vehicle (currently the head of its in-link) enter the node now?
    /// Controls never move vehicles; they only grant or withhold permission.
    /// </summary>
    public interface IIntersectionControl
    {
        void Tick(Simulation sim, Node node, float dt);
        bool MayEnter(Simulation sim, Node node, Vehicle v, Movement m);
        /// <summary>Called when a permitted vehicle actually crosses the line
        /// (stop-sign server consumes its grant here).</summary>
        void OnVehicleEntered(Simulation sim, Node node, Vehicle v, Movement m);
    }

    // =====================================================================
    //  Gap acceptance — ONE primitive, three consumers (permissive turns,
    //  two-way stops, roundabout entries).
    // =====================================================================
    public static class GapAcceptance
    {
        /// <summary>
        /// True iff, on every conflicting in-link, the nearest vehicle that is
        /// actually approaching the node is at least `thresholdSec` away
        /// (time-to-arrival at current speed). A stationary vehicle near the
        /// line blocks ONLY if its own intended movement conflicts with ours —
        /// otherwise two opposing permissive lefts would each wait on the other
        /// forever (single-lane mutual deadlock).
        /// </summary>
        public static bool Acceptable(Simulation sim, Node node, Movement m,
                                      IReadOnlyList<int> conflictingInLinks, float thresholdSec)
        {
            const float blockDist = 8f;
            for (int i = 0; i < conflictingInLinks.Count; i++)
            {
                int linkId = conflictingInLinks[i];
                if (linkId == m.InLink) continue;
                var link = sim.Network.LinkById(linkId);
                var front = link.Front;
                if (front == null) continue;
                float dist = link.Length - front.Pos;
                if (front.Speed < 0.3f)
                {
                    if (dist < blockDist)
                    {
                        // Which way is the stopped vehicle going? If its movement
                        // doesn't conflict with ours, it isn't in our path. Nor is
                        // it if a signal is holding it at the line (red or yellow):
                        // that is when a permissive left "sneaks" through.
                        var theirMove = front.OnLastLink ? null : node.FindMovement(linkId, front.NextLink);
                        if (theirMove != null && node.Conflicts[m.Index, theirMove.Index] &&
                            !(node.Control is SignalController sc && !sc.MovementGreen(theirMove.Index)))
                            return false;
                    }
                    continue;
                }
                float tta = dist / front.Speed;
                if (tta < thresholdSec) return false;
            }
            return true;
        }
    }

    // =====================================================================
    //  Uncontrolled — pass-through (boundary-adjacent nodes, diverges,
    //  roundabout exits).
    // =====================================================================
    public sealed class UncontrolledControl : IIntersectionControl
    {
        public static readonly UncontrolledControl Instance = new UncontrolledControl();
        public void Tick(Simulation sim, Node node, float dt) { }
        public bool MayEnter(Simulation sim, Node node, Vehicle v, Movement m) => true;
        public void OnVehicleEntered(Simulation sim, Node node, Vehicle v, Movement m) { }
    }

    // =====================================================================
    //  Signalized
    // =====================================================================
    public enum SignalState { Green, Yellow, AllRed }

    public sealed class SignalController : IIntersectionControl
    {
        public readonly List<PhaseDef> Phases;
        public readonly float MinGreen, YellowTime, AllRedTime, GapThreshold;

        public int CurrentPhase { get; private set; }
        public SignalState State { get; private set; } = SignalState.Green;
        public float TimeInState { get; private set; }
        public float TimeInPhase { get; private set; }   // since this phase last went green

        public ISignalPolicy Policy;
        public float DecisionInterval = 0.5f;
        private float _decisionTimer;
        private int _pendingPhase = -1;

        // Cached per-phase movement permission (rebuilt on phase entry).
        private bool[] _allowed;       // movement index -> allowed in current green
        private bool[] _permissive;    // movement index -> needs gap acceptance

        public SignalController(Node node, List<PhaseDef> phases,
                                float minGreen, float yellow, float allRed, float gapThreshold)
        {
            Phases = phases;
            MinGreen = minGreen; YellowTime = yellow; AllRedTime = allRed; GapThreshold = gapThreshold;
            _allowed = new bool[node.Movements.Count];
            _permissive = new bool[node.Movements.Count];
            EnterPhase(node, 0);
        }

        private void EnterPhase(Node node, int phase)
        {
            CurrentPhase = phase;
            State = SignalState.Green;
            TimeInState = 0f; TimeInPhase = 0f;
            Array.Clear(_allowed, 0, _allowed.Length);
            Array.Clear(_permissive, 0, _permissive.Length);
            var p = Phases[phase];
            for (int i = 0; i < p.movements.Count; i++) _allowed[p.movements[i]] = true;
            for (int i = 0; i < p.permissive.Count; i++) _permissive[p.permissive[i]] = true;
        }

        /// <summary>Policies and players call this; the controller decides when
        /// (or whether) to honor it. Safety envelope lives here, not in policies.</summary>
        public void RequestPhase(int phase)
        {
            if (phase == CurrentPhase && State == SignalState.Green) { _pendingPhase = -1; return; }
            if (phase < 0 || phase >= Phases.Count) return;
            _pendingPhase = phase;
        }

        public void Tick(Simulation sim, Node node, float dt)
        {
            TimeInState += dt; TimeInPhase += dt;

            if (Policy != null && State == SignalState.Green)
            {
                _decisionTimer -= dt;
                if (_decisionTimer <= 0f)
                {
                    _decisionTimer = DecisionInterval;
                    RequestPhase(Policy.SelectPhase(sim, node, this));
                }
            }

            switch (State)
            {
                case SignalState.Green:
                    if (_pendingPhase >= 0 && _pendingPhase != CurrentPhase && TimeInPhase >= MinGreen)
                    {
                        State = SignalState.Yellow; TimeInState = 0f;
                    }
                    break;
                case SignalState.Yellow:
                    if (TimeInState >= YellowTime) { State = SignalState.AllRed; TimeInState = 0f; }
                    break;
                case SignalState.AllRed:
                    if (TimeInState >= AllRedTime)
                    {
                        int next = _pendingPhase >= 0 ? _pendingPhase : (CurrentPhase + 1) % Phases.Count;
                        _pendingPhase = -1;
                        EnterPhase(node, next);
                    }
                    break;
            }
        }

        /// <summary>Presentation only: is movement `i` being served a green right
        /// now (protected or permissive)? Used to draw which approaches are green.</summary>
        public bool MovementGreen(int i) => State == SignalState.Green && _allowed[i];

        /// <summary>Presentation only: is movement `i` part of the CURRENT phase,
        /// regardless of green/yellow/all-red sub-state? The caller colours it by
        /// State so an approach shows green → amber (clearing) → red across a switch,
        /// instead of snapping to red the instant yellow begins.</summary>
        public bool MovementServed(int i) => _allowed[i];

        public bool MayEnter(Simulation sim, Node node, Vehicle v, Movement m)
        {
            if (!_allowed[m.Index]) return false;
            // Protected movements enter on green only. A permissive movement may
            // also clear on yellow, once the opposing traffic it yields to is held
            // at the line — the "sneaker" that keeps a single lane from being
            // blocked by one left-turner for a whole cycle.
            if (State == SignalState.AllRed) return false;
            if (State == SignalState.Yellow && !_permissive[m.Index]) return false;
            if (_permissive[m.Index])
            {
                // Permissive: yield to conflicting movements that are protected-green now.
                _conflictScratch.Clear();
                for (int i = 0; i < node.Movements.Count; i++)
                {
                    if (i == m.Index) continue;
                    if (_allowed[i] && !_permissive[i] && node.Conflicts[m.Index, i])
                        _conflictScratch.Add(node.Movements[i].InLink);
                }
                return GapAcceptance.Acceptable(sim, node, m, _conflictScratch, GapThreshold);
            }
            return true;
        }

        public void OnVehicleEntered(Simulation sim, Node node, Vehicle v, Movement m) { }

        private readonly List<int> _conflictScratch = new List<int>();
    }

    // =====================================================================
    //  All-way stop — full stop required, then first-come-first-served,
    //  one departure per ServiceTime.
    // =====================================================================
    public sealed class AllWayStopControl : IIntersectionControl
    {
        public readonly float ServiceTime;
        private readonly Queue<(long vehId, int inLink)> _fifo = new Queue<(long, int)>();
        private readonly HashSet<long> _enqueued = new HashSet<long>();
        private long _granted = -1;
        private float _nextServeTime;

        public AllWayStopControl(float serviceTime) { ServiceTime = serviceTime; }

        public void Tick(Simulation sim, Node node, float dt)
        {
            // Enqueue newly stop-ready head vehicles, deterministic in-link order.
            for (int i = 0; i < node.InLinks.Count; i++)
            {
                var link = sim.Network.LinkById(node.InLinks[i]);
                var front = link.Front;
                if (front != null && front.HasStopped && _enqueued.Add(front.Id))
                    _fifo.Enqueue((front.Id, link.Id));
            }
            // Grant the server to the FIFO head when free.
            if (_granted < 0 && _fifo.Count > 0 && sim.Time >= _nextServeTime)
            {
                // Drop stale entries (vehicle no longer the head of that link).
                while (_fifo.Count > 0)
                {
                    var head = _fifo.Peek();
                    var link = sim.Network.LinkById(head.inLink);
                    if (link.Front != null && link.Front.Id == head.vehId) { _granted = head.vehId; break; }
                    _enqueued.Remove(_fifo.Dequeue().vehId);
                }
            }
        }

        public bool MayEnter(Simulation sim, Node node, Vehicle v, Movement m)
            => v.HasStopped && v.Id == _granted;

        public void OnVehicleEntered(Simulation sim, Node node, Vehicle v, Movement m)
        {
            if (v.Id == _granted)
            {
                _granted = -1;
                _enqueued.Remove(_fifo.Dequeue().vehId);
                _nextServeTime = sim.Time + ServiceTime;
            }
        }
    }

    // =====================================================================
    //  Two-way stop — major road through/right never yields; a major-road
    //  LEFT gap-accepts against the opposing major approach (it crosses that
    //  traffic); minor road stops, then gap-accepts against the major in-links.
    // =====================================================================
    public sealed class TwoWayStopControl : IIntersectionControl
    {
        public readonly List<int> MajorInLinks;
        public readonly float GapThreshold;
        public TwoWayStopControl(List<int> majorInLinks, float gapThreshold)
        { MajorInLinks = majorInLinks ?? new List<int>(); GapThreshold = gapThreshold; }

        public void Tick(Simulation sim, Node node, float dt) { }

        public bool MayEnter(Simulation sim, Node node, Vehicle v, Movement m)
        {
            if (MajorInLinks.Contains(m.InLink))
                return m.Turn != TurnMask.Left || GapAcceptance.Acceptable(sim, node, m, MajorInLinks, GapThreshold);
            if (!v.HasStopped) return false;
            return GapAcceptance.Acceptable(sim, node, m, MajorInLinks, GapThreshold);
        }

        public void OnVehicleEntered(Simulation sim, Node node, Vehicle v, Movement m) { }
    }

    // =====================================================================
    //  Yield entry — roundabout merge: entering traffic yields (no full stop
    //  required) to circulating traffic; circulating traffic never yields.
    // =====================================================================
    public sealed class YieldEntryControl : IIntersectionControl
    {
        public readonly List<int> PriorityInLinks;   // the circulating link(s)
        public readonly float GapThreshold;
        public YieldEntryControl(List<int> priorityInLinks, float gapThreshold)
        { PriorityInLinks = priorityInLinks ?? new List<int>(); GapThreshold = gapThreshold; }

        public void Tick(Simulation sim, Node node, float dt) { }

        public bool MayEnter(Simulation sim, Node node, Vehicle v, Movement m)
        {
            if (PriorityInLinks.Contains(m.InLink)) return true;
            return GapAcceptance.Acceptable(sim, node, m, PriorityInLinks, GapThreshold);
        }

        public void OnVehicleEntered(Simulation sim, Node node, Vehicle v, Movement m) { }
    }
}
