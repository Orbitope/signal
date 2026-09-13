using System;
using System.IO;

namespace Signal.Core
{
    /// <summary>
    /// The trained policy's actor, as plain arithmetic: obs(233) → tanh(128) →
    /// tanh(128) → logits(8). Weights come from training/export_policy.py
    /// (little-endian float32; see FromBytes for the layout). No ONNX, no
    /// runtime; 94k parameters is a few hundred microseconds per decision.
    /// </summary>
    public sealed class PolicyWeights
    {
        public const uint Magic = 0x53474E4C;   // "LNGS"
        public string Tag = "";
        public int ObsSize, Hidden, Actions;
        public float[] W1, B1, W2, B2, W3, B3;   // W1: Hidden x ObsSize (row-major), W2: Hidden x Hidden, W3: Actions x Hidden

        public string Describe() => $"{Tag}: {ObsSize}->{Hidden}->{Hidden}->{Actions}";

        /// <summary>Layout: magic u32, version u32, tagLen u32 + utf8 tag, obs i32,
        /// hidden i32, actions i32, then W1 B1 W2 B2 W3 B3 as float32 arrays.</summary>
        public static PolicyWeights FromBytes(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a Signal policy file");
            int version = r.ReadInt32();
            if (version != 1) throw new InvalidDataException($"unsupported policy file version {version}");
            int tagLen = r.ReadInt32();
            var w = new PolicyWeights { Tag = System.Text.Encoding.UTF8.GetString(r.ReadBytes(tagLen)) };
            w.ObsSize = r.ReadInt32(); w.Hidden = r.ReadInt32(); w.Actions = r.ReadInt32();
            if (w.ObsSize != ObsSchema.Size || w.Actions != ObsSchema.MaxPhases)
                throw new InvalidDataException($"policy expects obs {w.ObsSize}/actions {w.Actions}; sim has {ObsSchema.Size}/{ObsSchema.MaxPhases}");
            w.W1 = Floats(r, w.Hidden * w.ObsSize); w.B1 = Floats(r, w.Hidden);
            w.W2 = Floats(r, w.Hidden * w.Hidden); w.B2 = Floats(r, w.Hidden);
            w.W3 = Floats(r, w.Actions * w.Hidden); w.B3 = Floats(r, w.Actions);
            return w;
        }

        private static float[] Floats(BinaryReader r, int n)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadSingle();
            return a;
        }

        /// <summary>Actor forward pass. Scratch buffers belong to the caller so a
        /// policy per signal can share weights without sharing state.</summary>
        public void Logits(float[] obs, float[] h1, float[] h2, float[] logits)
        {
            for (int j = 0; j < Hidden; j++)
            {
                float acc = B1[j]; int row = j * ObsSize;
                for (int i = 0; i < ObsSize; i++) acc += W1[row + i] * obs[i];
                h1[j] = (float)Math.Tanh(acc);
            }
            for (int j = 0; j < Hidden; j++)
            {
                float acc = B2[j]; int row = j * Hidden;
                for (int i = 0; i < Hidden; i++) acc += W2[row + i] * h1[i];
                h2[j] = (float)Math.Tanh(acc);
            }
            for (int a = 0; a < Actions; a++)
            {
                float acc = B3[a]; int row = a * Hidden;
                for (int i = 0; i < Hidden; i++) acc += W3[row + i] * h2[i];
                logits[a] = acc;
            }
        }

        /// <summary>Greedy action: argmax over legal phases (the evaluator's act_greedy).</summary>
        public int Greedy(float[] logits, byte[] mask)
        {
            int best = -1; float bestV = float.MinValue;
            for (int a = 0; a < Actions; a++)
                if (mask[a] != 0 && logits[a] > bestV) { bestV = logits[a]; best = a; }
            return best < 0 ? 0 : best;
        }
    }

    /// <summary>
    /// ISignalPolicy wrapper: builds this signal's observation with the same
    /// AgentView the trainer used (so the game and the env server agree on
    /// every float), runs the actor, and returns the greedy legal phase. The
    /// controller polls it every DecisionInterval; use 5 s to match the
    /// trainer's 50 decision ticks.
    /// </summary>
    public sealed class LearnedPolicy : ISignalPolicy
    {
        private readonly PolicyWeights _w;
        private AgentView _view;
        private readonly float[] _obs, _h1, _h2, _logits;
        private readonly byte[] _mask;

        public LearnedPolicy(PolicyWeights w)
        {
            _w = w;
            _obs = new float[w.ObsSize]; _h1 = new float[w.Hidden]; _h2 = new float[w.Hidden];
            _logits = new float[w.Actions]; _mask = new byte[w.Actions];
        }

        public int SelectPhase(Simulation sim, Node node, SignalController ctl)
        {
            if (_view == null || _view.Node != node) _view = new AgentView(sim, node, attach: false);
            _view.WriteObs(sim, _obs, 0);
            _view.WriteMask(_mask, 0);
            _w.Logits(_obs, _h1, _h2, _logits);
            return _w.Greedy(_logits, _mask);
        }
    }

    /// <summary>
    /// Which policy runs the lights in the game. Two brains for now, chosen by
    /// the level: a lone junction (World 1) gets the policy trained on
    /// w1-training; anything with more than one signal gets the grid policy
    /// from the article. Either falls back to aging MaxPressure when its
    /// weights are missing. The engine fills these in at startup.
    /// </summary>
    public static class GameAi
    {
        public static Func<SignalController, ISignalPolicy> Junction = ctl => new AgingMaxPressurePolicy();
        public static Func<SignalController, ISignalPolicy> Network = ctl => new AgingMaxPressurePolicy();
        public static string JunctionName = "aging MaxPressure";
        public static string NetworkName = "aging MaxPressure";

        public static int SignalCount(Simulation sim)
        {
            int n = 0;
            foreach (var node in sim.Network.Nodes) if (node.Control is SignalController) n++;
            return n;
        }

        /// <summary>Junctions a player could edit: not map edges, not lane forks,
        /// not roundabout entries. A level with one is World 1's shape whatever
        /// control it has; more than one is a network even if only one signal
        /// remains, because the junction brain never saw stop-sign neighbours.</summary>
        public static int JunctionCount(Simulation sim)
        {
            int n = 0;
            foreach (var node in sim.Network.Nodes)
                if (!node.IsBoundary && node.InLinks.Count >= 3 && !(node.Control is YieldEntryControl)) n++;
            return n;
        }

        public static Func<SignalController, ISignalPolicy> FactoryFor(Simulation sim)
            => JunctionCount(sim) > 1 ? Network : Junction;

        public static string NameFor(Simulation sim) => JunctionCount(sim) > 1 ? NetworkName : JunctionName;
    }
}
