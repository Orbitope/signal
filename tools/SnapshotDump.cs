using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Signal.Core;

var level = new LevelDef
{
    network = LaneBuilder.FourWayWithBays(throughLanes: 2),
    demand = NetworkBuilder.SymmetricDemand(44f)
};
var sim = new Simulation(level, 20260817);
var ctl = sim.ControllerAt(0);
var mp = new MaxPressurePolicy();
ctl.Policy = mp;
ctl.DecisionInterval = 5f;

object Snap(string label)
{
    var links = sim.Network.Links.Select(l => new
    {
        id = l.Id, from = l.From, to = l.To, length = l.Length, turns = l.Turns.ToString(),
        fx = sim.Network.NodeById(l.From).X, fy = sim.Network.NodeById(l.From).Y,
        tx = sim.Network.NodeById(l.To).X, ty = sim.Network.NodeById(l.To).Y,
        vehicles = l.Vehicles.Select(v => new { pos = v.Pos, len = v.Length, wait = v.Wait, speed = v.Speed }).ToList()
    }).ToList();
    var phase = ctl.Phases[ctl.CurrentPhase];
    var node = sim.Network.NodeById(0);
    var allowedIn = new HashSet<int>();
    foreach (int mi in phase.movements) allowedIn.Add(node.Movements[mi].InLink);
    var gates = sim.Demand.EntryQueues.ToDictionary(kv => kv.Key, kv => kv.Value.Count);

    // Real agent observation + mask at this exact moment (hijack policy, restore).
    var savedPolicy = ctl.Policy; var savedInterval = ctl.DecisionInterval;
    var av = new AgentView(sim, node);
    var obs = new float[ObsSchema.Size];
    var mask = new byte[ObsSchema.MaxPhases];
    av.WriteObs(sim, obs, 0);
    av.WriteMask(mask, 0);
    float tickReward = av.TickPressure(sim);
    ctl.Policy = savedPolicy; ctl.DecisionInterval = savedInterval;

    return new
    {
        label, t = sim.Time, phase = ctl.CurrentPhase, state = ctl.State.ToString(),
        timeInPhase = ctl.TimeInPhase, allowedInLinks = allowedIn.ToList(), gates, links,
        obs, mask, tickReward
    };
}

var snaps = new List<object>();
bool gotThrough = false, gotLeft = false;
for (int i = 0; i < 30000 && (!gotThrough || !gotLeft); i++)
{
    sim.Step();
    if (sim.Time < 120) continue;
    if (!gotThrough && ctl.CurrentPhase == 0 && ctl.State == SignalState.Green && ctl.TimeInPhase > 3f)
    { snaps.Add(Snap("NS through green")); gotThrough = true; }
    if (!gotLeft && ctl.CurrentPhase == 1 && ctl.State == SignalState.Green && ctl.TimeInPhase > 2f)
    { snaps.Add(Snap("NS protected lefts green")); gotLeft = true; }
}

var center = sim.Network.NodeById(0);
var movements = center.Movements.Select(m => new { idx = m.Index, inLink = m.InLink, outLink = m.OutLink, turn = m.Turn.ToString() }).ToList();
var conflicts = new List<int[]>();
for (int a = 0; a < center.Movements.Count; a++)
    for (int b = a + 1; b < center.Movements.Count; b++)
        if (center.Conflicts[a, b]) conflicts.Add(new[] { a, b });
File.WriteAllText("/tmp/snap.json", JsonSerializer.Serialize(new
{ snaps, movements, conflicts, phases = ctl.Phases.Select(p => p.movements).ToList() }));

// ---- scaling bench: per-intersection cost of lanes ----
double Bench(NetworkDef net, DemandDef dem)
{
    var s = new Simulation(new LevelDef { network = net, demand = dem }, 3);
    foreach (var n in s.Network.Nodes)
        if (n.Control is SignalController c) { c.Policy = new MaxPressurePolicy(); c.DecisionInterval = 5f; }
    for (int i = 0; i < 2000; i++) s.Step();
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 30000; i++) s.Step();
    return 30000.0 / sw.Elapsed.TotalSeconds;
}
double single = Bench(NetworkBuilder.FourWay(ControlType.Signalized), NetworkBuilder.SymmetricDemand(24f));
double bays = Bench(LaneBuilder.FourWayWithBays(), NetworkBuilder.SymmetricDemand(30f));
double bays2 = Bench(LaneBuilder.FourWayWithBays(throughLanes: 2), NetworkBuilder.SymmetricDemand(40f));
Console.WriteLine($"steps/sec  single {single:N0} | bays {bays:N0} | bays2 {bays2:N0}");
Console.WriteLine($"cost ratio vs single: bays {single / bays:F2}x  bays2 {single / bays2:F2}x");
