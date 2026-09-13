using System;

class Sizing
{
    static long Mlp(int inDim, int[] hidden, int outDim)
    {
        long p = 0; int prev = inDim;
        foreach (int h in hidden) { p += (long)prev * h + h; prev = h; }
        p += (long)prev * outDim + outDim;
        return p;
    }

    static void Row(string name, int inDim, int[] hidden, int outDim, int copies)
    {
        long per = Mlp(inDim, hidden, outDim);
        long total = per * copies;
        string hs = string.Join("x", hidden);
        Console.WriteLine($"{name,-34} {inDim,5} {hs,9} {outDim,5} {per,10:N0} {copies,5} {total,12:N0} {total * 4 / 1024.0 / 1024.0,8:F2} MB");
    }

    static void Main()
    {
        Console.WriteLine("=== Actor parameter counts (PPO policy head; critic is a separate same-size trunk) ===\n");
        Console.WriteLine($"{"config",-34} {"obs",5} {"hidden",9} {"act",5} {"per-net",10} {"nets",5} {"total",12} {"fp32",11}");

        // Rung 1: independent learners, local obs only, one net per intersection.
        int localObs = 4 * 3 + 4 + 1;                 // approaches(q,wait,valid) + phase onehot + timeInPhase
        Console.WriteLine("\n-- Rung 1: independent learners (one net per light, local obs) --");
        foreach (int n in new[] { 4, 9, 25 })
            Row($"IQL 64x64, {n} lights", localObs, new[] { 64, 64 }, 4, n);

        // Rung 2: centralized controller, global obs, factored per-intersection heads.
        Console.WriteLine("\n-- Rung 2: centralized joint controller (global obs, N action heads) --");
        foreach (int n in new[] { 4, 9, 25 })
        {
            int globalObs = localObs * n;
            Row($"central 512x512, {n} lights", globalObs, new[] { 512, 512 }, 4 * n, 1);
        }
        Console.WriteLine("   (a single categorical over joint actions would be 4^N:");
        foreach (int n in new[] { 4, 9, 25 })
            Console.WriteLine($"      {n} lights -> {Math.Pow(4, n):E2} joint actions  <- why factored heads are mandatory)");

        // Rung 3: shared local policy, padded obs + neighbors, ONE net for all.
        Console.WriteLine("\n-- Rung 3: shared local policy (padded obs + neighbors, one net total) --");
        int sharedObs = 4 * 3 + 4 + 1 + 4 * (4 + 4 + 1 + 4 + 1);   // 73
        foreach (var h in new[] { new[] { 64, 64 }, new[] { 128, 128 }, new[] { 256, 256 } })
            Row($"shared {h[0]}x{h[1]} (any N)", sharedObs, h, 4, 1);

        // ---------------------------------------------------------------
        Console.WriteLine("\n\n=== Experience throughput: why sharing wins on sample efficiency ===\n");
        Console.WriteLine("Assumptions: DT=0.1s, decision every 5 sim-sec (=50 steps), 5M tuples to converge.\n");
        Console.WriteLine($"{"grid",-8} {"lights",7} {"steps/sec",11} {"tuples/sec",12} {"per-net rate",14} {"wall-clock to 5M",18}");

        // Measured throughput from the scaling benchmark (near-capacity, 1 core).
        var measured = new (string grid, int lights, double sps)[]
        { ("1x1", 1, 345000), ("2x2", 4, 38000), ("3x3", 9, 16800), ("5x5", 25, 10000) };

        foreach (var (grid, lights, sps) in measured)
        {
            double decisionsPerSimSec = lights / 5.0;
            double simSecPerWallSec = sps * 0.1;
            double tuplesPerSec = decisionsPerSimSec * simSecPerWallSec;
            // Shared: all tuples train ONE net. Independent: each net gets 1/lights of them.
            double sharedHours = 5e6 / tuplesPerSec / 3600;
            double indepHours = 5e6 / (tuplesPerSec / lights) / 3600;
            Console.WriteLine($"{grid,-8} {lights,7} {sps,11:N0} {tuplesPerSec,12:N0} " +
                              $"{tuplesPerSec / lights,10:N0}/net  shared {sharedHours,5:F2}h | indep {indepHours,6:F1}h");
        }

        Console.WriteLine("\n(single core, single env. 16 parallel envs divides wall-clock by ~16.)");
    }
}
