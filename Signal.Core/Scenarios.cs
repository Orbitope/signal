using System.Collections.Generic;
using static Signal.Core.DowntownBuilder;

namespace Signal.Core
{
    /// <summary>
    /// The complex city scenarios. Each returns a full LevelDef (network + demand);
    /// the seven grid-expressible ones are live here, the two that need schema/phase
    /// extensions (superstreet, diagonal) are documented in training/scenarios.md and
    /// drawn as schematics in the report. All reuse DowntownBuilder's phase derivation.
    /// </summary>
    public static class Scenarios
    {
        const TurnMask NoLeft = TurnMask.Through | TurnMask.Right;
        const TurnMask RightOnly = TurnMask.Right;
        const TurnMask Any = TurnMask.All;

        public static LevelDef Get(string name) => name switch
        {
            "sc-couplet" => CoupletGrid(),
            "sc-onewaypair" => OneWayPair(),
            "sc-riro" => Riro(),
            "sc-diverge" => Diverge(),
            "sc-tidal" => Tidal(),
            "sc-platoon" => Platoon(),
            "sc-mixed" => MixedControl(),
            "sc-diagonal" => DiagonalBuilder.Diagonal(),
            _ => null
        };

        public static readonly string[] Names =
            { "sc-couplet", "sc-onewaypair", "sc-riro", "sc-diverge",
              "sc-tidal", "sc-platoon", "sc-mixed", "sc-diagonal" };

        // 1 — Downtown one-way couplet grid: every street one-way, alternating
        //     direction on parallel streets, lefts banned everywhere.
        static LevelDef CoupletGrid()
        {
            int n = 5;
            var h = new StreetSpec[n]; var v = new StreetSpec[n];
            for (int r = 0; r < n; r++) h[r] = new StreetSpec(r % 2 == 0 ? Flow.Fwd : Flow.Bwd, NoLeft);
            for (int c = 0; c < n; c++) v[c] = new StreetSpec(c % 2 == 0 ? Flow.Fwd : Flow.Bwd, NoLeft);
            var net = Build(n, n, h, v);
            EntriesExits(n, n, h, v, out var en, out var ex);
            var d = new DemandDef();
            AddBackground(d, en, ex, 3.0f, 44, seed: 101);
            return new LevelDef { name = "sc-couplet", network = net, demand = d };
        }

        // 2 — One-way pair (opposite directions) crossed by two-way thoroughfares
        //     (no left turns off the thoroughfares).
        static LevelDef OneWayPair()
        {
            int cols = 5, rows = 4;
            var h = new StreetSpec[rows]; var v = new StreetSpec[cols];
            h[0] = new StreetSpec(Flow.TwoWay, Any);
            h[1] = new StreetSpec(Flow.Fwd, NoLeft);           // couplet eastbound
            h[2] = new StreetSpec(Flow.Bwd, NoLeft);           // couplet westbound
            h[3] = new StreetSpec(Flow.TwoWay, Any);
            for (int c = 0; c < cols; c++)
                v[c] = (c == 1 || c == 3) ? new StreetSpec(Flow.TwoWay, NoLeft, art: true)
                                          : new StreetSpec(Flow.TwoWay, Any);
            var net = Build(cols, rows, h, v);
            var d = new DemandDef();
            d.flows.Add(F(WB(1), EB(1), 20f));                 // couplet E
            d.flows.Add(F(EB(2), WB(2), 20f));                 // couplet W
            foreach (int c in new[] { 1, 3 }) {                // thoroughfares
                d.flows.Add(F(SB(c), NB(c), 14f)); d.flows.Add(F(NB(c), SB(c), 12f));
            }
            EntriesExits(cols, rows, h, v, out var en, out var ex);
            AddBackground(d, en, ex, 2.0f, 20, seed: 202);
            return new LevelDef { name = "sc-onewaypair", network = net, demand = d };
        }

        // 3 — Access-managed arterial: fast two-way arterial (no lefts), and the
        //     side streets entering it are right-in/right-out only.
        static LevelDef Riro()
        {
            int cols = 6, rows = 3;
            var h = new StreetSpec[rows]; var v = new StreetSpec[cols];
            h[0] = new StreetSpec(Flow.TwoWay, Any);
            h[1] = new StreetSpec(Flow.TwoWay, NoLeft, art: true);   // arterial
            h[2] = new StreetSpec(Flow.TwoWay, Any);
            for (int c = 0; c < cols; c++) v[c] = new StreetSpec(Flow.TwoWay, Any);
            var net = Build(cols, rows, h, v);
            // RIRO: the side-street approaches into the arterial node (1,c) may only
            // turn right; the arterial itself keeps through+right.
            for (int c = 0; c < cols; c++)
            {
                SetLinkTurns(net, 0 * cols + c, 1 * cols + c, RightOnly);   // from south
                SetLinkTurns(net, 2 * cols + c, 1 * cols + c, RightOnly);   // from north
            }
            DeriveTwoPhase(net);
            var d = new DemandDef();
            d.flows.Add(F(WB(1), EB(1), 22f)); d.flows.Add(F(EB(1), WB(1), 20f));  // arterial
            // side trips along the arterial frontage (routable via rights)
            d.flows.Add(F(SB(0), EB(1), 6f)); d.flows.Add(F(SB(5), WB(1), 6f));
            d.flows.Add(F(WB(0), EB(2), 5f)); d.flows.Add(F(WB(2), EB(0), 5f));
            return new LevelDef { name = "sc-riro", network = net, demand = d };
        }

        // 5 — Diverge/converge couplet: a two-way arterial spine with a one-way
        //     pair on either side (traffic splits around the block).
        static LevelDef Diverge()
        {
            int cols = 5, rows = 4;
            var h = new StreetSpec[rows]; var v = new StreetSpec[cols];
            for (int r = 0; r < rows; r++) h[r] = new StreetSpec(Flow.TwoWay, Any);
            v[0] = new StreetSpec(Flow.TwoWay, Any);
            v[1] = new StreetSpec(Flow.Fwd, NoLeft);           // one-way northbound
            v[2] = new StreetSpec(Flow.TwoWay, NoLeft, art: true); // spine
            v[3] = new StreetSpec(Flow.Bwd, NoLeft);           // one-way southbound
            v[4] = new StreetSpec(Flow.TwoWay, Any);
            var net = Build(cols, rows, h, v);
            var d = new DemandDef();
            d.flows.Add(F(SB(2), NB(2), 20f)); d.flows.Add(F(NB(2), SB(2), 18f)); // spine
            d.flows.Add(F(SB(1), NB(1), 12f));                 // northbound couplet leg
            d.flows.Add(F(NB(3), SB(3), 12f));                 // southbound couplet leg
            EntriesExits(cols, rows, h, v, out var en, out var ex);
            AddBackground(d, en, ex, 2.0f, 18, seed: 505);
            return new LevelDef { name = "sc-diverge", network = net, demand = d };
        }

        // 6 — Tidal one-way pair: rush-hour reverses the dominant direction over
        //     the episode (time-varying demand on the couplet).
        static LevelDef Tidal()
        {
            int cols = 5, rows = 4;
            var h = new StreetSpec[rows]; var v = new StreetSpec[cols];
            h[0] = new StreetSpec(Flow.TwoWay, Any);
            h[1] = new StreetSpec(Flow.Fwd, NoLeft);
            h[2] = new StreetSpec(Flow.Bwd, NoLeft);
            h[3] = new StreetSpec(Flow.TwoWay, Any);
            for (int c = 0; c < cols; c++) v[c] = new StreetSpec(Flow.TwoWay, Any);
            var net = Build(cols, rows, h, v);
            var d = new DemandDef();
            // AM peak dominates eastbound; PM peak dominates westbound (opposite curves).
            d.flows.Add(FRush(WB(1), EB(1), 26f));                              // rises then falls
            d.flows.Add(new OdFlowDef { origin = EB(2), dest = WB(2), rate = new RateCurve {
                times = { 0f, 180f, 330f, 600f }, rates = { 24f, 6f, 6f, 24f } } });  // inverse
            EntriesExits(cols, rows, h, v, out var en, out var ex);
            AddBackground(d, en, ex, 1.6f, 16, seed: 606);
            return new LevelDef { name = "sc-tidal", network = net, demand = d };
        }

        // 7 — Platoon surge: one boundary dumps sharp pulses (a metered ramp)
        //     onto a one-way grid; the signals must flush and progress the platoon.
        static LevelDef Platoon()
        {
            int n = 5;
            var h = new StreetSpec[n]; var v = new StreetSpec[n];
            for (int r = 0; r < n; r++) h[r] = new StreetSpec(r % 2 == 0 ? Flow.Fwd : Flow.Bwd, NoLeft);
            for (int c = 0; c < n; c++) v[c] = new StreetSpec(Flow.TwoWay, Any);
            var net = Build(n, n, h, v);
            var d = new DemandDef();
            // ramp platoons: three sharp pulses from the SW corner boundary.
            d.flows.Add(new OdFlowDef { origin = SB(0), dest = NB(4), rate = new RateCurve {
                times = { 0f, 40f, 60f, 200f, 220f, 380f, 400f, 600f },
                rates = { 4f, 4f, 40f, 4f, 40f, 4f, 40f, 4f } } });
            EntriesExits(n, n, h, v, out var en, out var ex);
            AddBackground(d, en, ex, 1.6f, 20, seed: 707);
            return new LevelDef { name = "sc-platoon", network = net, demand = d };
        }

        // 8 — Heterogeneous control: a signalized grid with a thoroughfare, but
        //     several minor crossings are all-way stops or yield-controlled.
        static LevelDef MixedControl()
        {
            int cols = 5, rows = 4;
            var h = new StreetSpec[rows]; var v = new StreetSpec[cols];
            for (int r = 0; r < rows; r++)
                h[r] = r == 1 ? new StreetSpec(Flow.TwoWay, NoLeft, art: true)
                              : new StreetSpec(Flow.TwoWay, Any);
            for (int c = 0; c < cols; c++) v[c] = new StreetSpec(Flow.TwoWay, Any);
            // minor crossings away from the arterial become stops / yields
            var ctl = new Dictionary<int, ControlType> {
                { 0 * cols + 1, ControlType.AllWayStop },
                { 0 * cols + 3, ControlType.AllWayStop },
                { 3 * cols + 1, ControlType.YieldEntry },
                { 3 * cols + 3, ControlType.AllWayStop },
            };
            var net = Build(cols, rows, h, v, ctl);
            var d = new DemandDef();
            d.flows.Add(F(WB(1), EB(1), 20f)); d.flows.Add(F(EB(1), WB(1), 18f)); // arterial
            EntriesExits(cols, rows, h, v, out var en, out var ex);
            AddBackground(d, en, ex, 2.2f, 24, seed: 808);
            return new LevelDef { name = "sc-mixed", network = net, demand = d };
        }
    }
}
