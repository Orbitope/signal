using Godot;
using System.Collections.Generic;
using Signal.Core;
using SimCore = Signal.Core.Simulation;

namespace SignalGodot
{
    /// <summary>
    /// The ONLY place Godot time meets sim time. The sim runs at a fixed 10 Hz
    /// (SimConfig.DT); rendering runs at frame rate and interpolates between the
    /// last two sim states. Views read from this node; nothing writes to Core
    /// except phase requests through the ExternalPolicy.
    /// </summary>
    public partial class SimRunner : Godot.Node
    {
        [Export] public float TimeScale = 1f;      // 0 = paused (planning mode)

        public SimCore Sim { get; private set; }
        public SimCore Ghost { get; private set; } // lockstep MaxPressure opponent
        public ExternalPolicy PlayerPolicy { get; private set; }
        public float Alpha { get; private set; }   // interpolation fraction for views

        private float _accumulator;

        // Interpolation snapshots: vehicle id -> (linkId, pos) at the last two ticks.
        private readonly Dictionary<long, (int link, float pos)> _prev = new();
        private readonly Dictionary<long, (int link, float pos)> _curr = new();

        [Signal] public delegate void SpillbackEventHandler(int linkId);

        public void Load(LevelDef level, ulong seed)
        {
            Sim = new SimCore(level, seed);
            foreach (var node in Sim.Network.Nodes)
                if (node.Control is SignalController ctl)
                {
                    PlayerPolicy = new ExternalPolicy();
                    ctl.Policy = PlayerPolicy;
                    ctl.DecisionInterval = 0.2f;   // near-immediate response to taps
                }

            // Ghost: identical seed and demand, MaxPressure everywhere. "The AI
            // faced identical traffic" is true by construction, and it costs ~30
            // lines instead of an ONNX runtime dependency.
            Ghost = new SimCore(level, seed);
            foreach (var node in Ghost.Network.Nodes)
                if (node.Control is SignalController ctl)
                { ctl.Policy = new MaxPressurePolicy(); ctl.DecisionInterval = 5f; }

            Sim.SpillbackStarted += linkId =>
                CallDeferred(Godot.Node.MethodName.EmitSignal, SignalName.Spillback, linkId);

            Snapshot(_curr);
            Snapshot(_prev);
        }

        public override void _Process(double delta)
        {
            if (Sim == null || TimeScale <= 0f) { return; }
            _accumulator += (float)delta * TimeScale;

            // Cap catch-up work per frame so a hitch can't spiral.
            int safety = 30;
            while (_accumulator >= SimConfig.DT && safety-- > 0)
            {
                _accumulator -= SimConfig.DT;
                _prev.Clear();
                foreach (var kv in _curr) _prev[kv.Key] = kv.Value;
                Sim.Step();
                Ghost.Step();
                Snapshot(_curr);
            }
            Alpha = Mathf.Clamp(_accumulator / SimConfig.DT, 0f, 1f);
        }

        private void Snapshot(Dictionary<long, (int, float)> dst)
        {
            dst.Clear();
            foreach (var link in Sim.Network.Links)
                for (int i = 0; i < link.Vehicles.Count; i++)
                    dst[link.Vehicles[i].Id] = (link.Id, link.Vehicles[i].Pos);
        }

        /// <summary>Interpolated (linkId, pos) for every live vehicle. Vehicles
        /// that changed link between ticks snap (no cross-node interpolation).</summary>
        public IEnumerable<(long id, int link, float pos)> InterpolatedVehicles()
        {
            foreach (var kv in _curr)
            {
                if (_prev.TryGetValue(kv.Key, out var prev) && prev.link == kv.Value.link)
                    yield return (kv.Key, kv.Value.link, Mathf.Lerp(prev.pos, kv.Value.pos, Alpha));
                else
                    yield return (kv.Key, kv.Value.link, kv.Value.pos);
            }
        }

        /// <summary>Tap an approach: request the phase that serves that in-link.</summary>
        public void RequestGreenFor(int nodeId, int inLinkId)
        {
            var node = Sim.Network.NodeById(nodeId);
            if (node.Control is not SignalController ctl || PlayerPolicy == null) return;
            for (int p = 0; p < ctl.Phases.Count; p++)
                foreach (int mi in ctl.Phases[p].movements)
                    if (node.Movements[mi].InLink == inLinkId)
                    { PlayerPolicy.Request(p); ctl.RequestPhase(p); return; }
        }
    }
}
