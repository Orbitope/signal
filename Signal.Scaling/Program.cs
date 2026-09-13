using System;
using System.Diagnostics;
using System.Collections.Generic;
using Signal.Core;

// Measures the three things that could scale badly with "lots of lights":
//   1. model size        (parameter sharing => constant, computed not measured)
//   2. inference cost    (batched forward passes per decision tick)
//   3. simulation cost   (the actual bottleneck)
class Scaling
{
    public static void Main()
    {
        Console.WriteLine("grid   lights  links   demand    veh@end   steps/sec   xRealtime   decisions/s");
        foreach (var (n, load) in new[] { (1,14f),(2,14f),(3,14f),(4,14f),(5,14f),(3,4f),(5,4f),(5,2f) })
        {
            var net = GridBuilder.Grid(n);
            var demand = GridBuilder.GridDemand(n, load);
            var level = new LevelDef { network = net, demand = demand, duration = 600 };
            var sim = new Simulation(level, 42);

            int lights = 0;
            foreach (var node in sim.Network.Nodes)
                if (node.Control is SignalController ctl)
                {
                    ctl.Policy = new MaxPressurePolicy();
                    ctl.DecisionInterval = 5f;
                    lights++;
                }

            // Warm up so JIT isn't in the measurement.
            for (int i = 0; i < 2000; i++) sim.Step();

            var sw = Stopwatch.StartNew();
            int steps = 30000;
            for (int i = 0; i < steps; i++) sim.Step();
            sw.Stop();

            double sps = steps / sw.Elapsed.TotalSeconds;
            double realtime = sps * SimConfig.DT;
            double decisionsPerSec = lights / 5.0;   // one batched decision per light per 5 sim-sec

            Console.WriteLine($"{n}x{n,-4} {lights,6} {sim.Network.Links.Count,6} " +
                              $"{load * n * n,7:F0}/m {sim.VehiclesInSystem(),9} " +
                              $"{sps,11:N0} {realtime,10:N0}x {decisionsPerSec,12:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("Shared-policy MLP: 73 -> 256 -> 256 -> 4");
        long p = 73L * 256 + 256 + 256L * 256 + 256 + 256L * 4 + 4;
        Console.WriteLine($"  parameters: {p:N0}   fp32: {p * 4 / 1024.0:F0} KB   int8: {p / 1024.0:F0} KB");
        Console.WriteLine("  (constant regardless of intersection count - parameter sharing)");

        // How long does one batched forward pass actually take? Simulate the
        // matmuls directly to get an order of magnitude without an ONNX dep.
        foreach (int batch in new[] { 1, 25, 100 })
        {
            var x = new float[batch * 73];
            var w1 = new float[73 * 256]; var w2 = new float[256 * 256]; var w3 = new float[256 * 4];
            var h1 = new float[batch * 256]; var h2 = new float[batch * 256]; var outp = new float[batch * 4];
            var rng = new Rng(1);
            for (int i = 0; i < w1.Length; i++) w1[i] = (float)rng.Range(-0.1, 0.1);
            for (int i = 0; i < w2.Length; i++) w2[i] = (float)rng.Range(-0.1, 0.1);

            var sw2 = Stopwatch.StartNew();
            int iters = 2000;
            for (int it = 0; it < iters; it++)
            {
                Matmul(x, w1, h1, batch, 73, 256); Relu(h1);
                Matmul(h1, w2, h2, batch, 256, 256); Relu(h2);
                Matmul(h2, w3, outp, batch, 256, 4);
            }
            sw2.Stop();
            double us = sw2.Elapsed.TotalMilliseconds * 1000 / iters;
            Console.WriteLine($"  batch {batch,3}: {us,7:F1} us per forward pass (naive C#, no SIMD/ONNX)");
        }
    }

    static void Matmul(float[] a, float[] w, float[] o, int batch, int inDim, int outDim)
    {
        Array.Clear(o, 0, batch * outDim);
        for (int b = 0; b < batch; b++)
            for (int i = 0; i < inDim; i++)
            {
                float av = a[b * inDim + i];
                if (av == 0f) continue;
                int wo = i * outDim, oo = b * outDim;
                for (int j = 0; j < outDim; j++) o[oo + j] += av * w[wo + j];
            }
    }
    static void Relu(float[] v) { for (int i = 0; i < v.Length; i++) if (v[i] < 0) v[i] = 0; }
}
