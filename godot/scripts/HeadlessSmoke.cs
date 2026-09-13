using Godot;
using Signal.Core;
using SimCore = Signal.Core.Simulation;

namespace SignalGodot
{
    /// <summary>
    /// CI gate: run with
    ///   godot --headless --path . res://scenes/smoke.tscn
    /// Steps the sim inside Godot's .NET runtime and exits nonzero on failure.
    /// Verifies what the engine port could plausibly break (Core loads,
    /// determinism survives Godot's runtime, ghost stays lockstep) plus the P0
    /// level-player contract: built-in levels load through the registry and run,
    /// and a tap overrides exactly one signal and then hands back to the AI.
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
                { c.Policy = new MaxPressurePolicy(); c.DecisionInterval = 5f; }
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

            // 6) A tap overrides only its own signal, and expires back to MaxPressure.
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

            GD.Print(failures == 0 ? "SMOKE PASSED" : $"{failures} SMOKE FAILURES");
            GetTree().Quit(failures);
        }
    }
}
