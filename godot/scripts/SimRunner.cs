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
    /// except phase requests through each signal's TapOverridePolicy.
    ///
    /// Two ways to load:
    /// - Sandbox: the game AI drives every light, taps override one at a time,
    ///   and a lockstep ghost (plain game AI, same seed) is the opponent.
    /// - Puzzle: the level already has the player's edits applied; policies
    ///   come from Edits.AttachPolicies (AI everywhere, timed plans where the
    ///   player set one), no taps, no ghost.
    /// </summary>
    public partial class SimRunner : Godot.Node
    {
        [Export] public float TimeScale = 1f;      // 0 = paused (planning mode)
        [Export] public float DecisionInterval = 5f;   // MaxPressure needs ~5 s or it thrashes

        public SimCore Sim { get; private set; }
        public SimCore Ghost { get; private set; } // lockstep opponent (sandbox only; null in puzzles)
        public LevelDef Level { get; private set; }
        public ulong Seed { get; private set; }
        public float Alpha { get; private set; }   // interpolation fraction for views
        public bool Finished { get; private set; }

        private readonly Dictionary<int, TapOverridePolicy> _tap = new();
        private List<EditOp> _ops;
        private bool _withGhost = true, _withTaps = true;
        private float _accumulator;

        // Interpolation snapshots: vehicle id -> (linkId, pos) at the last two ticks.
        private readonly Dictionary<long, (int link, float pos)> _prev = new();
        private readonly Dictionary<long, (int link, float pos)> _curr = new();

        [Signal] public delegate void SpillbackEventHandler(int linkId);
        [Signal] public delegate void FinishedRoundEventHandler();

        /// <summary>Sandbox load: game AI + taps + ghost.</summary>
        public void Load(LevelDef level, ulong seed) => Load(level, seed, null, true, true);

        public void Load(LevelDef level, ulong seed, IList<EditOp> ops, bool withGhost, bool withTaps)
        {
            Level = level; Seed = seed;
            _ops = ops == null ? null : new List<EditOp>(ops);
            _withGhost = withGhost; _withTaps = withTaps;
            Finished = false; _accumulator = 0f; Alpha = 0f;
            _tap.Clear();

            Sim = new SimCore(level, seed);
            var ai = GameAi.FactoryFor(Sim);
            if (_ops != null) Edits.AttachPolicies(Sim, _ops, DecisionInterval);
            else
                foreach (var node in Sim.Network.Nodes)
                    if (node.Control is SignalController ctl)
                    { ctl.Policy = ai(ctl); ctl.DecisionInterval = DecisionInterval; }

            if (_withTaps)
                foreach (var node in Sim.Network.Nodes)
                    if (node.Control is SignalController ctl && !(ctl.Policy is FixedTimePolicy))
                    {
                        var p = new TapOverridePolicy(ctl.Policy);
                        _tap[node.Id] = p;
                        ctl.Policy = p;
                        ctl.DecisionInterval = DecisionInterval;
                    }

            Ghost = null;
            if (_withGhost)
            {
                Ghost = new SimCore(level, seed);
                foreach (var node in Ghost.Network.Nodes)
                    if (node.Control is SignalController ctl)
                    { ctl.Policy = ai(ctl); ctl.DecisionInterval = DecisionInterval; }
            }

            Sim.SpillbackStarted += linkId =>
                CallDeferred(Godot.Node.MethodName.EmitSignal, SignalName.Spillback, linkId);

            Snapshot(_curr);
            Snapshot(_prev);
        }

        /// <summary>No level loaded: views draw nothing.</summary>
        public void Clear()
        {
            Sim = null; Ghost = null; Level = null; Finished = false;
            _tap.Clear(); _prev.Clear(); _curr.Clear(); _accumulator = 0f;
        }

        /// <summary>Same level, same seed, same policies, from the top.</summary>
        public void Reset() { if (Level != null) Load(Level, Seed, _ops, _withGhost, _withTaps); }

        public override void _Process(double delta)
        {
            if (Sim == null || TimeScale <= 0f || Finished) return;
            _accumulator += (float)delta * TimeScale;

            // Cap catch-up work per frame so a hitch can't spiral; at high time
            // scales the cap is what bounds frame time, not the accumulator.
            int safety = TimeScale >= 8f ? 120 : 30;
            while (_accumulator >= SimConfig.DT && safety-- > 0)
            {
                _accumulator -= SimConfig.DT;
                _prev.Clear();
                foreach (var kv in _curr) _prev[kv.Key] = kv.Value;
                Sim.Step();
                Ghost?.Step();
                Snapshot(_curr);
                if (Sim.Time >= Level.duration)
                {
                    Finished = true;
                    CallDeferred(Godot.Node.MethodName.EmitSignal, SignalName.FinishedRound);
                    break;
                }
            }
            if (_accumulator > SimConfig.DT * 4f) _accumulator = SimConfig.DT * 4f;   // drop, don't hoard
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

        /// <summary>Tap an approach: request the phase that serves that in-link
        /// on that node only, applied immediately and held for a while.</summary>
        public void RequestGreenFor(int nodeId, int inLinkId)
        {
            var node = Sim.Network.NodeById(nodeId);
            if (node.Control is not SignalController ctl || !_tap.TryGetValue(nodeId, out var tap)) return;
            for (int p = 0; p < ctl.Phases.Count; p++)
                foreach (int mi in ctl.Phases[p].movements)
                    if (node.Movements[mi].InLink == inLinkId)
                    {
                        tap.Request(p, Sim.Time);
                        ctl.RequestPhase(p);
                        return;
                    }
        }

        public bool IsOverriding(int nodeId)
            => Sim != null && _tap.TryGetValue(nodeId, out var t) && t.IsOverriding(Sim.Time);

        public float OverrideRemaining(int nodeId)
            => Sim != null && _tap.TryGetValue(nodeId, out var t) ? t.Remaining(Sim.Time) : 0f;

        public int SignalCount => Sim == null ? 0 : GameAi.SignalCount(Sim);
        public string AiName => Sim == null ? "" : GameAi.NameFor(Sim);
    }
}
