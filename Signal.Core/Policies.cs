using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// A policy answers "which phase do you want?" every decision interval.
    /// It cannot violate min-green/yellow/all-red — the controller enforces those.
    /// FixedTime is also the player's authored plan in planning mode; External is
    /// the seam for player taps and ML actions.
    /// </summary>
    public interface ISignalPolicy
    {
        int SelectPhase(Simulation sim, Node node, SignalController ctl);
    }

    public sealed class FixedTimePolicy : ISignalPolicy
    {
        public float Cycle;        // seconds
        public float[] Splits;     // fraction per phase, sums to 1
        public float Offset;       // seconds

        public FixedTimePolicy(float cycle, float[] splits, float offset = 0f)
        { Cycle = cycle; Splits = splits; Offset = offset; }

        public int SelectPhase(Simulation sim, Node node, SignalController ctl)
        {
            float lt = ((sim.Time - Offset) % Cycle + Cycle) % Cycle;
            float acc = 0f;
            for (int i = 0; i < Splits.Length; i++)
            {
                acc += Splits[i] * Cycle;
                if (lt < acc) return i;
            }
            return Splits.Length - 1;
        }
    }

    /// <summary>Serve the phase whose movements have the most queued vehicles,
    /// with hysteresis so it doesn't flap between near-equal queues.</summary>
    public sealed class GreedyPolicy : ISignalPolicy
    {
        public int HysteresisVehicles = 2;

        public int SelectPhase(Simulation sim, Node node, SignalController ctl)
        {
            int best = ctl.CurrentPhase;
            int bestQ = PhaseQueue(sim, node, ctl, ctl.CurrentPhase) + HysteresisVehicles;
            for (int p = 0; p < ctl.Phases.Count; p++)
            {
                if (p == ctl.CurrentPhase) continue;
                int q = PhaseQueue(sim, node, ctl, p);
                if (q > bestQ) { bestQ = q; best = p; }
            }
            return best;
        }

        internal static int PhaseQueue(Simulation sim, Node node, SignalController ctl, int phase)
        {
            int q = 0;
            var mv = ctl.Phases[phase].movements;
            _seen.Clear();
            for (int i = 0; i < mv.Count; i++)
            {
                int inLink = node.Movements[mv[i]].InLink;
                if (_seen.Add(inLink))                      // single lane: count each in-link once
                    q += sim.Network.LinkById(inLink).QueueCount();
            }
            return q;
        }
        private static readonly HashSet<int> _seen = new HashSet<int>();
    }

    /// <summary>
    /// MaxPressure (Varaiya): phase pressure = Σ over movements of
    /// (upstream queue − downstream occupancy). Provably throughput-optimal
    /// under assumptions, and the classical baseline your RL agent must beat.
    /// </summary>
    public sealed class MaxPressurePolicy : ISignalPolicy
    {
        /// <summary>Pressure advantage required to justify paying the
        /// yellow+all-red switching cost. In vehicles.</summary>
        public float SwitchStickiness = 2f;

        public int SelectPhase(Simulation sim, Node node, SignalController ctl)
        {
            int best = ctl.CurrentPhase; float bestP = float.MinValue;
            for (int p = 0; p < ctl.Phases.Count; p++)
            {
                float pressure = PhasePressure(sim, node, ctl, p);
                if (p == ctl.CurrentPhase) pressure += SwitchStickiness;
                if (pressure > bestP) { bestP = pressure; best = p; }
            }
            return best;
        }

        public static float PhasePressure(Simulation sim, Node node, SignalController ctl, int phase)
        {
            float sum = 0f;
            var mv = ctl.Phases[phase].movements;
            _seenIn.Clear();
            for (int i = 0; i < mv.Count; i++)
            {
                var m = node.Movements[mv[i]];
                if (_seenIn.Add(m.InLink))                       // single lane: one queue per in-link
                    sum += sim.Network.LinkById(m.InLink).QueueCount();
                sum -= sim.Network.LinkById(m.OutLink).QueueCount() * 0.5f;
            }
            return sum;
        }
        private static readonly HashSet<int> _seenIn = new HashSet<int>();
    }

    /// <summary>Externally driven phase requests: player taps in tactical mode,
    /// or ML-Agents actions via the adapter. Holds the last request.</summary>
    public sealed class ExternalPolicy : ISignalPolicy
    {
        private int _requested;
        public void Request(int phase) => _requested = phase;
        public int SelectPhase(Simulation sim, Node node, SignalController ctl) => _requested;
    }
}
