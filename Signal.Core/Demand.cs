using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// Demand: per-flow Bernoulli thinning of a time-varying Poisson rate.
    /// Each tick, each flow spawns with p = rate(t)/60 * DT. Deterministic given
    /// the seed and fixed flow iteration order.
    ///
    /// Spawned vehicles that can't fit on their entry link join a per-origin
    /// virtual entry queue where wait time still accrues — demand is never
    /// silently dropped (that would make congestion look cheaper than it is,
    /// the same starvation-masking bug class as counting only despawned waits).
    /// </summary>
    public sealed class DemandSource
    {
        private readonly DemandDef _def;
        private readonly Router _router;
        private readonly Rng _rng;
        private long _nextVehId;
        public float RateMultiplier = 1f;   // DemandRandomizer hook for training

        public readonly Dictionary<int, Queue<Vehicle>> EntryQueues = new Dictionary<int, Queue<Vehicle>>();

        public DemandSource(DemandDef def, Router router, Rng rng)
        {
            _def = def; _router = router; _rng = rng;
            foreach (var f in def.flows)
                if (!EntryQueues.ContainsKey(f.origin))
                    EntryQueues[f.origin] = new Queue<Vehicle>();
        }

        public void Tick(Simulation sim, float dt)
        {
            // 1) Sample arrivals.
            for (int i = 0; i < _def.flows.Count; i++)
            {
                var f = _def.flows[i];
                float ratePerSec = f.rate.Evaluate(sim.Time) / 60f * RateMultiplier;
                if (ratePerSec <= 0f) continue;
                if (_rng.NextDouble() < ratePerSec * dt)
                {
                    var route = _router.Route(f.origin, f.dest);
                    if (route == null) continue;   // validator should have caught this
                    EntryQueues[f.origin].Enqueue(new Vehicle
                    {
                        Id = _nextVehId++, Route = route, RouteIdx = 0,
                        Pos = 0f, Speed = 0f, SpawnTime = sim.Time
                    });
                }
            }

            // 2) Release entry-queue heads onto their first link when space exists.
            foreach (var kv in EntryQueues)
            {
                var q = kv.Value;
                if (q.Count == 0) continue;
                var v = q.Peek();
                var link = sim.Network.LinkById(v.Route[0]);
                if (link.HasEntrySpace(v.Length))
                {
                    q.Dequeue();
                    v.Pos = v.Length;                       // front bumper just inside
                    v.Speed = Math.Min(link.SpeedLimit * 0.5f, 8f);
                    link.Vehicles.Add(v);                   // enters at the rear (smallest Pos)
                    sim.OnVehicleSpawned(v);
                }
                else
                {
                    // Held at the gate: wait accrues for everyone in the queue.
                    foreach (var w in q) w.Wait += dt;
                }
            }
        }

        public int HeldCount()
        {
            int n = 0; foreach (var kv in EntryQueues) n += kv.Value.Count; return n;
        }

        public void AccumulateHeldWait(ref float sum, ref int count)
        {
            foreach (var kv in EntryQueues)
                foreach (var v in kv.Value) { sum += v.Wait; count++; }
        }
    }

    /// <summary>Edge-based Dijkstra over free-flow travel times: the search state
    /// is a LINK, and transitions are the node's derived MOVEMENTS. Routes
    /// therefore respect turn masks (no through trips via a left-only bay) and
    /// U-turn exclusions structurally. Cached per OD pair; routes are fixed at
    /// spawn except for same-bundle lane choice at forks (see Simulation).</summary>
    public sealed class Router
    {
        private readonly RoadNetwork _net;
        private readonly Dictionary<(int, int), int[]> _cache = new Dictionary<(int, int), int[]>();

        public Router(RoadNetwork net) { _net = net; }

        public int[] Route(int originNode, int destNode)
        {
            var key = (originNode, destNode);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var dist = new Dictionary<int, float>();      // link id -> cost to traverse up to link end
            var prev = new Dictionary<int, int>();        // link id -> predecessor link id
            var pq = new SortedSet<(float d, int link)>();

            foreach (int startId in _net.NodeById(originNode).OutLinks)
            {
                var l = _net.LinkById(startId);
                float c = l.Length / l.SpeedLimit;
                dist[startId] = c;
                pq.Add((c, startId));
            }

            int goal = -1;
            while (pq.Count > 0)
            {
                var (d, u) = pq.Min; pq.Remove(pq.Min);
                if (d > dist[u] + 1e-6f) continue;        // stale entry
                var link = _net.LinkById(u);
                if (link.To == destNode) { goal = u; break; }
                var node = _net.NodeById(link.To);
                foreach (var m in node.Movements)
                {
                    if (m.InLink != u) continue;
                    var next = _net.LinkById(m.OutLink);
                    float nd = d + next.Length / next.SpeedLimit;
                    if (!dist.TryGetValue(m.OutLink, out float old) || nd < old - 1e-6f)
                    {
                        if (dist.TryGetValue(m.OutLink, out float o)) pq.Remove((o, m.OutLink));
                        dist[m.OutLink] = nd; prev[m.OutLink] = u;
                        pq.Add((nd, m.OutLink));
                    }
                }
            }

            if (goal < 0) { _cache[key] = null; return null; }

            var route = new List<int>();
            for (int cur = goal; ; )
            {
                route.Add(cur);
                if (!prev.TryGetValue(cur, out cur)) break;
            }
            route.Reverse();
            var arr = route.ToArray();
            _cache[key] = arr;
            return arr;
        }

        public void InvalidateCache() => _cache.Clear();
    }
}
