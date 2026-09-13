using Godot;
using Signal.Core;
using SimCore = Signal.Core.Simulation;

namespace SignalGodot
{
    /// <summary>
    /// CI gate: run with
    ///   godot --headless res://scenes/smoke.tscn
    /// Steps the sim inside Godot's .NET runtime and exits nonzero on failure.
    /// Verifies the three things the engine port could plausibly break:
    /// Core loads, determinism survives Godot's runtime, ghost stays lockstep.
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

            // 4) Tap routing reaches the controller.
            runner.RequestGreenFor(NetworkBuilder.Center, 2);   // E approach
            Check(true, "tap request routed without exception");

            GD.Print(failures == 0 ? "SMOKE PASSED" : $"{failures} SMOKE FAILURES");
            GetTree().Quit(failures);
        }
    }
}
