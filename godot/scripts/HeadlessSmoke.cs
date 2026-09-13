using Godot;
using Signal.Core;
using System.Collections.Generic;
using SimCore = Signal.Core.Simulation;

namespace SignalGodot
{
    /// <summary>
    /// CI gate: run with
    ///   godot --headless --path . res://scenes/smoke.tscn
    /// Steps the sim inside Godot's .NET runtime and exits nonzero on failure.
    /// Verifies what the engine port could plausibly break (Core loads,
    /// determinism survives Godot's runtime, ghost stays lockstep), the P0
    /// level-player contract (registry levels run; a tap overrides one signal
    /// and hands back), and the P1 puzzle contract (edit ops build legal
    /// levels, every World 1 puzzle fails as given and solves with its
    /// authored answer, the runner honours a timed plan).
    /// </summary>
    public partial class HeadlessSmoke : Godot.Node
    {
        public override void _Ready()
        {
            int failures = 0;
            void Check(bool ok, string name)
            {
                GD.Print(ok ? $"  PASS  {name}" : $"  FAIL  {name}");
                if (!ok) failures++;
            }

            GD.Print("== Godot headless smoke ==");

            // 1) Core simulates inside Godot's runtime.
            var level = new LevelDef
            {
                network = NetworkBuilder.FourWay(ControlType.Signalized),
                demand = NetworkBuilder.SymmetricDemand(24f)
            };
            var sim = new SimCore(level, 42);
            foreach (var n in sim.Network.Nodes)
                if (n.Control is SignalController c)
                { c.Policy = new AgingMaxPressurePolicy(); c.DecisionInterval = 5f; }
            for (int i = 0; i < 6000; i++) sim.Step();
            Check(sim.Metrics.Completed > 100, $"traffic flows (completed={sim.Metrics.Completed})");

            // 2) Determinism inside this runtime matches itself.
            var a = new SimCore(level, 7); var b = new SimCore(level, 7);
            foreach (var n in a.Network.Nodes) if (n.Control is SignalController c) c.Policy = new FixedTimePolicy(30f, new[] { .5f, .5f });
            foreach (var n in b.Network.Nodes) if (n.Control is SignalController c) c.Policy = new FixedTimePolicy(30f, new[] { .5f, .5f });
            for (int i = 0; i < 6000; i++) { a.Step(); b.Step(); }
            Check(a.StateHash() == b.StateHash(), $"determinism hash ({a.StateHash():x16})");

            // 3) SimRunner ghost stays lockstep under uneven frame deltas.
            var runner = new SimRunner();
            AddChild(runner);
            runner.Load(level, 99);
            var deltas = new double[] { 0.016, 0.033, 0.2, 0.008, 0.1, 0.016, 0.05 };
            double fed = 0;
            for (int i = 0; i < 300; i++) { runner._Process(deltas[i % deltas.Length]); fed += deltas[i % deltas.Length]; }
            long expected = (long)(fed / SimConfig.DT);
            Check(runner.Sim.StepCount == runner.Ghost.StepCount,
                  $"ghost lockstep ({runner.Sim.StepCount} steps both)");
            Check(System.Math.Abs(runner.Sim.StepCount - expected) <= 2,
                  $"fixed-tick accounting ({runner.Sim.StepCount} steps for {fed:F1}s fed, expected ~{expected})");

            // 4) A tap reaches the controller and marks that signal as overridden.
            var center = runner.Sim.Network.NodeById(NetworkBuilder.Center);
            runner.RequestGreenFor(NetworkBuilder.Center, center.InLinks[0]);
            Check(runner.IsOverriding(NetworkBuilder.Center), "tap override active on the tapped signal");

            // 5) P0: multi-signal built-in levels load through the registry and run.
            foreach (var name in new[] { "grid3", "sc-couplet", "corridor" })
            {
                var r = new SimRunner();
                AddChild(r);
                r.Load(LevelLoader.Load(name), 5);
                for (int i = 0; i < 900; i++) r._Process(0.1);   // 90 sim-seconds
                Check(r.Sim.Metrics.Completed > 0 && r.Sim.VehiclesInSystem() > 0 && r.SignalCount > 1,
                      $"{name} runs ({r.SignalCount} signals, {r.Sim.Metrics.Completed} done, {r.Sim.VehiclesInSystem()} in system)");
            }

            // 6) A tap overrides only its own signal, and expires back to the AI.
            {
                var r = new SimRunner();
                AddChild(r);
                r.Load(LevelLoader.Load("grid2"), 3);
                int first = -1, second = -1;
                foreach (var n in r.Sim.Network.Nodes)
                    if (n.Control is SignalController) { if (first < 0) first = n.Id; else if (second < 0) second = n.Id; }
                var node = r.Sim.Network.NodeById(first);
                r.RequestGreenFor(first, node.InLinks[0]);
                Check(r.IsOverriding(first), "tap overrides its own signal");
                Check(!r.IsOverriding(second), "tap does not touch other signals");
                for (int i = 0; i < 140; i++) r._Process(0.1);   // 14 s > 12 s hold
                Check(!r.IsOverriding(first), "override expires and hands back to the AI");
            }

            // 7) A round finishes at the level's duration and fires exactly once.
            {
                var r = new SimRunner();
                AddChild(r);
                var shortLevel = LevelLoader.Load("fourway");
                shortLevel.duration = 20f;
                r.Load(shortLevel, 11);
                for (int i = 0; i < 400; i++) r._Process(0.1);   // 40 s fed, should stop at 20 s
                Check(r.Finished && r.Sim.Time >= 20f && r.Sim.Time < 21f,
                      $"round finishes at duration (t={r.Sim.Time:F1}s)");
            }

            // 8) P1: edit ops build legal levels.
            {
                var four = Levels.Get("fourway");
                var ring = Edits.Apply(four, new[] { new EditOp { kind = EditKind.Roundabout, node = NetworkBuilder.Center } });
                int yields = ring.network.nodes.FindAll(n => n.control == ControlType.YieldEntry).Count;
                Check(yields == 4 && ring.network.nodes.Find(n => n.id == NetworkBuilder.Center) == null && ring.network.links.Count == four.network.links.Count + 4,
                      $"roundabout macro: 4 yield entries, 4 arcs, centre removed ({yields} yields, {ring.network.links.Count} links)");

                var bays = Edits.Apply(four, new[] { new EditOp { kind = EditKind.AddBay, link = 1 }, new EditOp { kind = EditKind.AddBay, link = 3 } });
                var c0 = bays.network.nodes.Find(n => n.id == NetworkBuilder.Center);
                Check(bays.network.links.Count == four.network.links.Count + 4 && c0.phases.Count == 3,
                      $"bays on N and S: 2 lanes each, 3-phase plan (N-S through, N-S lefts, E-W) ({bays.network.links.Count} links, {c0.phases.Count} phases)");

                bool refused = false;
                try { Edits.Apply(four, new[] { new EditOp { kind = EditKind.TurnBan, link = 1, turns = TurnMask.Through } }); }
                catch (PuzzleException) { refused = true; }
                Check(refused, "an edit that strands a demand flow is refused");

                var sol = new Solution();
                sol.Add(new EditOp { kind = EditKind.SetControl, node = 0, control = ControlType.AllWayStop });
                sol.Add(new EditOp { kind = EditKind.SetControl, node = 0, control = ControlType.Signalized });
                Check(sol.Ops.Count == 1 && sol.Ops[0].control == ControlType.Signalized, "a second control on the same junction replaces the first");
            }

            // 9) P1: every World 1 puzzle fails as given and solves with its authored answer.
            foreach (var p in Worlds.All[0].puzzles)
            {
                var given = PuzzleScorer.Evaluate(p, p.initialOps);
                var answer = PuzzleScorer.Evaluate(p, p.answer);
                Check(given.Error == null && !given.Solved, $"{p.id} as given is not solved ({given.Summary()})");
                Check(answer.Error == null && answer.Solved && answer.Stars == 3 && answer.Cost <= p.budget,
                      $"{p.id} authored answer solves with 3 stars for ${answer.Cost}");
            }

            // 10) P1: the runner honours a timed plan and runs without a ghost.
            {
                var p = Worlds.Find("w1-6");
                var lv = Edits.Apply(p.level, p.initialOps);
                var r = new SimRunner();
                AddChild(r);
                r.Load(lv, PuzzleScorer.SeedFor(p, 0), p.initialOps, withGhost: false, withTaps: false);
                var ctl = r.Sim.ControllerAt(NetworkBuilder.Center);
                Check(r.Ghost == null && ctl.Policy is FixedTimePolicy, "puzzle load: no ghost, timed plan attached");
                for (int i = 0; i < 300; i++) r._Process(0.1);
                Check(r.Sim.Metrics.Completed > 0, $"puzzle level runs ({r.Sim.Metrics.Completed} done)");
            }

            GD.Print(failures == 0 ? "SMOKE PASSED" : $"{failures} SMOKE FAILURES");
            GetTree().Quit(failures);
        }
    }
}
