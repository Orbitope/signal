using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Signal.Core;

// =============================================================================
//  signal-envserver — vectorized RL environment over stdio.
//
//  PROTOCOL (both directions length-prefixed: 4-byte LE uint32 frame length):
//    client -> server: UTF-8 JSON only.
//      {"cmd":"reset","level":"fourway-bays","n_envs":8,"seed":1,
//       "decision_ticks":50,"episode_steps":120,"demand_lo":0.7,"demand_hi":1.2}
//      {"cmd":"step","actions":[[p,...]  x n_envs]}   (row length = n_agents)
//      {"cmd":"close"}
//    server -> client: one JSON header frame, then ONE binary frame:
//      header: {"n_envs":E,"n_agents":A,"obs_size":105,
//               "obs_bytes":..,"rew_bytes":..,"mask_bytes":..,"done_bytes":..}
//      binary: float32 obs[E*A*105] | float32 rew[E*A] | uint8 mask[E*A*8]
//              | uint8 done[E]   (concatenated in that order)
//
//  Rationale: JSON for control (tiny, debuggable), raw float32 for tensors
//  (numpy frombuffer, no parse). stdio because the trainer owns the process
//  lifecycle — no ports, no discovery, dies with its parent.
//
//  Episodes auto-reset per env: env i, episode k runs seed
//  base + i*1000 + k*7919 (documented, reproducible).
// =============================================================================

class EnvHost
{
    public Simulation Sim;
    public List<AgentView> Agents = new();
    public int EpisodeStep;
    public int Episode;

    private readonly string _level;
    private readonly ulong _baseSeed;
    private readonly int _index;
    private readonly float _demandLo, _demandHi;

    public EnvHost(string level, ulong baseSeed, int index, float demandLo, float demandHi)
    {
        _level = level; _baseSeed = baseSeed; _index = index;
        _demandLo = demandLo; _demandHi = demandHi;
        Reset();
    }

    public void Reset()
    {
        ulong seed = _baseSeed + (ulong)_index * 1000UL + (ulong)Episode * 7919UL;
        var level = Levels.Get(_level);
        Sim = new Simulation(level, seed);
        // Demand randomization: deterministic per episode from the same seed.
        var r = new Rng(seed ^ 0x5EEDUL);
        Sim.Demand.RateMultiplier = (float)r.Range(_demandLo, _demandHi);

        Agents.Clear();
        foreach (var node in Sim.Network.Nodes)
            if (node.Control is SignalController)
                Agents.Add(new AgentView(Sim, node));
        EpisodeStep = 0;
        Episode++;
    }
}

static class Levels
{
    public static LevelDef Get(string name) => name switch
    {
        "fourway" => new LevelDef { network = NetworkBuilder.FourWay(ControlType.Signalized),
                                    demand = NetworkBuilder.SymmetricDemand(22f) },
        "fourway-bays" => new LevelDef { network = LaneBuilder.FourWayWithBays(),
                                         demand = NetworkBuilder.SymmetricDemand(30f) },
        "fourway-bays2" => new LevelDef { network = LaneBuilder.FourWayWithBays(throughLanes: 2),
                                          demand = NetworkBuilder.SymmetricDemand(40f) },
        "grid2" => new LevelDef { network = GridBuilder.Grid(2), demand = GridBuilder.GridDemand(2, 12f) },
        "grid3" => new LevelDef { network = GridBuilder.Grid(3), demand = GridBuilder.GridDemand(3, 12f) },
        "grid5" => new LevelDef { network = GridBuilder.Grid(5), demand = GridBuilder.GridDemand(5, 12f) },
        // Corridor: real road hierarchy — fast arterials, a one-way side street,
        // demand concentrated on the thoroughfares. 5x3 = 15 intersections.
        "corridor" => new LevelDef { network = CorridorBuilder.Corridor(5, 3),
                                     demand = CorridorBuilder.CorridorDemand(5, 3) },
        "corridor-rush" => new LevelDef { network = CorridorBuilder.Corridor(5, 3),
                                          demand = CorridorBuilder.CorridorDemand(5, 3, rush: true) },
        "corridor7" => new LevelDef { network = CorridorBuilder.Corridor(7, 3),
                                      demand = CorridorBuilder.CorridorDemand(7, 3) },
        _ when name.StartsWith("sc-") => Scenarios.Get(name)
                                         ?? throw new ArgumentException($"unknown scenario '{name}'"),
        _ when name.StartsWith("file:") =>
            JsonSerializer.Deserialize<LevelDef>(File.ReadAllText(name.Substring(5)),
                new JsonSerializerOptions { IncludeFields = true }),
        _ => throw new ArgumentException($"unknown level '{name}'")
    };
}

class Program
{
    static BinaryWriter _out;
    static BinaryReader _in;

    static void Main(string[] args)
    {
        // Offline helper: `--dump <level>` writes the built LevelDef as JSON to
        // stdout and exits, so tooling can render the true authored topology.
        if (args.Length >= 2 && args[0] == "--dump")
        {
            var lvl = Levels.Get(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(lvl,
                new JsonSerializerOptions { IncludeFields = true, WriteIndented = false }));
            return;
        }

        _out = new BinaryWriter(Console.OpenStandardOutput());
        _in = new BinaryReader(Console.OpenStandardInput());

        List<EnvHost> envs = null;
        int decisionTicks = 50;
        int episodeSteps = 120;
        bool trace = false;      // opt-in render-frame recording (env 0 only)
        int frameEvery = 2;      // capture a snapshot every N sim ticks

        while (true)
        {
            var msg = ReadJson();
            if (msg == null) return;
            string cmd = msg.RootElement.GetProperty("cmd").GetString();

            if (cmd == "close") return;

            if (cmd == "reset")
            {
                var r = msg.RootElement;
                string level = r.GetProperty("level").GetString();
                int nEnvs = r.GetProperty("n_envs").GetInt32();
                ulong seed = r.TryGetProperty("seed", out var sv) ? sv.GetUInt64() : 1UL;
                decisionTicks = r.TryGetProperty("decision_ticks", out var dt) ? dt.GetInt32() : 50;
                episodeSteps = r.TryGetProperty("episode_steps", out var es) ? es.GetInt32() : 120;
                float lo = r.TryGetProperty("demand_lo", out var lv) ? lv.GetSingle() : 1f;
                float hi = r.TryGetProperty("demand_hi", out var hv) ? hv.GetSingle() : 1f;
                trace = r.TryGetProperty("trace", out var tv) && tv.GetBoolean();
                frameEvery = r.TryGetProperty("frame_every", out var fe) ? fe.GetInt32() : 2;

                envs = new List<EnvHost>();
                for (int i = 0; i < nEnvs; i++)
                    envs.Add(new EnvHost(level, seed, i, lo, hi));
                SendState(envs, null);
                if (trace) SendTraceMeta(envs[0], frameEvery);
                continue;
            }

            if (cmd == "step")
            {
                var acts = msg.RootElement.GetProperty("actions");
                var rewards = new float[envs.Count * envs[0].Agents.Count];
                var dones = new byte[envs.Count];
                var frames = trace ? new List<object>() : null;

                for (int e = 0; e < envs.Count; e++)
                {
                    var env = envs[e];
                    var row = acts[e];
                    for (int a = 0; a < env.Agents.Count; a++)
                        env.Agents[a].Act(row[a].GetInt32());

                    for (int t = 0; t < decisionTicks; t++)
                    {
                        env.Sim.Step();
                        for (int a = 0; a < env.Agents.Count; a++)
                            rewards[e * env.Agents.Count + a] += env.Agents[a].TickPressure(env.Sim);
                        if (trace && e == 0 && t % frameEvery == 0)
                            frames.Add(CaptureFrame(env));
                    }
                    for (int a = 0; a < env.Agents.Count; a++)
                        rewards[e * env.Agents.Count + a] *= ObsSchema.RewardScale;

                    env.EpisodeStep++;
                    if (env.EpisodeStep >= episodeSteps)
                    { dones[e] = 1; env.Reset(); }   // obs sent below is the fresh episode's t=0
                }
                SendState(envs, rewards, dones);
                if (trace)
                    WriteFrame(JsonSerializer.SerializeToUtf8Bytes(new
                    { frames = frames, done = dones[0] == 1 }));
                continue;
            }

            throw new InvalidOperationException($"unknown cmd '{cmd}'");
        }
    }

    static void SendState(List<EnvHost> envs, float[] rewards, byte[] dones = null)
    {
        int E = envs.Count, A = envs[0].Agents.Count, O = ObsSchema.Size;
        var obs = new float[E * A * O];
        var mask = new byte[E * A * ObsSchema.MaxPhases];
        rewards ??= new float[E * A];
        dones ??= new byte[E];

        for (int e = 0; e < E; e++)
        for (int a = 0; a < A; a++)
        {
            envs[e].Agents[a].WriteObs(envs[e].Sim, obs, (e * A + a) * O);
            envs[e].Agents[a].WriteMask(mask, (e * A + a) * ObsSchema.MaxPhases);
        }

        int obsBytes = obs.Length * 4, rewBytes = rewards.Length * 4;
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            n_envs = E, n_agents = A, obs_size = O, n_actions = ObsSchema.MaxPhases,
            // schema layout so the trainer/eval derive indices instead of hardcoding
            max_approaches = ObsSchema.MaxApproaches, approach_floats = ObsSchema.ApproachFloats,
            self_floats = ObsSchema.SelfFloats, neighbor_floats = ObsSchema.NeighborFloats,
            obs_bytes = obsBytes, rew_bytes = rewBytes,
            mask_bytes = mask.Length, done_bytes = dones.Length
        });
        WriteFrame(header);

        var bin = new byte[obsBytes + rewBytes + mask.Length + dones.Length];
        Buffer.BlockCopy(obs, 0, bin, 0, obsBytes);
        Buffer.BlockCopy(rewards, 0, bin, obsBytes, rewBytes);
        Buffer.BlockCopy(mask, 0, bin, obsBytes + rewBytes, mask.Length);
        Buffer.BlockCopy(dones, 0, bin, obsBytes + rewBytes + mask.Length, dones.Length);
        WriteFrame(bin);
    }

    // -- render trace (opt-in; env 0 only) ----------------------------------
    // A frame is the minimal dynamic state a browser needs to draw the moving
    // network: every vehicle as (linkId, position/1000 along its link), each
    // signal's phase + colour state, and a metrics snapshot. Static geometry
    // (node x,y, link endpoints) comes from `--dump`, not from here.
    static void SendTraceMeta(EnvHost env, int frameEvery)
    {
        var nodeIds = new int[env.Agents.Count];
        for (int a = 0; a < env.Agents.Count; a++) nodeIds[a] = env.Agents[a].Node.Id;
        WriteFrame(JsonSerializer.SerializeToUtf8Bytes(new
        {
            trace_meta = true, node_ids = nodeIds, frame_every = frameEvery,
            dt = SimConfig.DT, decision_ticks = 0
        }));
    }

    static object CaptureFrame(EnvHost env)
    {
        var sim = env.Sim;
        var v = new List<int>();
        var q = new List<int>();     // per-vehicle stopped flag (order matches v)
        int queued = 0;
        foreach (var link in sim.Network.Links)
        {
            var vs = link.Vehicles;
            float invLen = 1f / Math.Max(link.Length, 0.001f);
            for (int i = 0; i < vs.Count; i++)
            {
                var veh = vs[i];
                int pos = (int)Math.Round(Math.Min(1f, Math.Max(0f, veh.Pos * invLen)) * 1000f);
                v.Add(link.Id); v.Add(pos);
                bool stopped = veh.Speed < SimConfig.QueueSpeed;
                q.Add(stopped ? 1 : 0);
                if (stopped) queued++;
            }
        }
        int A = env.Agents.Count;
        var s = new int[A]; var p = new int[A];
        var green = new List<int>();     // incoming link ids currently served a green
        for (int a = 0; a < A; a++)
        {
            var ctl = env.Agents[a].Ctl;
            var node = env.Agents[a].Node;
            s[a] = (int)ctl.State; p[a] = ctl.CurrentPhase;
            // links served by the current phase, regardless of green/yellow/all-red —
            // the widget colours each by this node's State (green vs amber vs red).
            foreach (var mv in node.Movements)
                if (ctl.MovementServed(mv.Index)) green.Add(mv.InLink);
        }
        var m = sim.Metrics;
        return new
        {
            v, q, s, p, gl = green,
            m = new
            {
                cl = m.Completed,
                tp = (float)Math.Round(m.ThroughputPerMin(sim), 2),
                aw = (float)Math.Round(m.LiveAvgWait(sim), 3),
                insys = sim.VehiclesInSystem(),
                sb = m.SpillbackEvents,
                qd = queued
            }
        };
    }

    static void WriteFrame(byte[] payload)
    {
        _out.Write((uint)payload.Length);
        _out.Write(payload);
        _out.Flush();
    }

    static JsonDocument ReadJson()
    {
        try
        {
            uint len = _in.ReadUInt32();
            var buf = _in.ReadBytes((int)len);
            return JsonDocument.Parse(buf);
        }
        catch (EndOfStreamException) { return null; }
    }
}
