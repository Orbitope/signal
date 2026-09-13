using System;
using System.Collections.Generic;

namespace Signal.Core
{
    /// <summary>
    /// The research-standard intersection: each approach forks ~60m upstream
    /// into a LEFT-TURN BAY plus one or more THROUGH/RIGHT lanes — every lane
    /// is a link, lane choice happens at the fork, no mid-link lane changing.
    ///
    /// Node ids: center 0; boundaries N=100,E=101,S=102,W=103 (same as
    /// NetworkBuilder.FourWay so the same DemandDefs run unchanged);
    /// forks 20..23 (N,E,S,W).
    /// Link ids per approach i (0=N,1=E,2=S,3=W):
    ///   arm in    1+i      boundary -> fork
    ///   bay       31+i     fork -> center   (turns = Left)
    ///   through j 41+i+10j fork -> center   (turns = Through|Right)
    ///   arm out   11+i     center -> boundary
    /// </summary>
    public static class LaneBuilder
    {
        public const int N = NetworkBuilder.N, E = NetworkBuilder.E,
                         S = NetworkBuilder.S, W = NetworkBuilder.W;
        public const int Center = 0;

        public static NetworkDef FourWayWithBays(int throughLanes = 1,
            float armLength = 150f, float bayLength = 60f, PhaseScheme scheme = PhaseScheme.ProtectedLefts)
        {
            if (throughLanes < 1 || throughLanes > 3)
                throw new ArgumentException("1..3 through lanes");

            var def = new NetworkDef();
            def.nodes.Add(new NodeDef { id = Center, x = 0, y = 0, control = ControlType.Signalized });
            int[] arms = { N, E, S, W };
            (float x, float y)[] apos = { (0, armLength), (armLength, 0), (0, -armLength), (-armLength, 0) };

            for (int i = 0; i < 4; i++)
            {
                def.nodes.Add(new NodeDef { id = arms[i], x = apos[i].x, y = apos[i].y, isBoundary = true });
                // Fork sits bayLength upstream of the center along the approach.
                float fx = apos[i].x * (bayLength / armLength);
                float fy = apos[i].y * (bayLength / armLength);
                def.nodes.Add(new NodeDef { id = 20 + i, x = fx, y = fy });

                def.links.Add(new LinkDef { id = 1 + i, from = arms[i], to = 20 + i, length = armLength - bayLength });
                def.links.Add(new LinkDef { id = 31 + i, from = 20 + i, to = Center, length = bayLength, turns = TurnMask.Left });
                for (int j = 0; j < throughLanes; j++)
                    def.links.Add(new LinkDef { id = 41 + i + 10 * j, from = 20 + i, to = Center, length = bayLength, turns = TurnMask.Through | TurnMask.Right });
                def.links.Add(new LinkDef { id = 11 + i, from = Center, to = arms[i], length = armLength });
            }

            def.nodes[0].phases = BuildPhases(def, scheme);
            return def;
        }

        public enum PhaseScheme { ProtectedLefts, PermissiveLefts }

        /// <summary>
        /// ProtectedLefts — the standard 4-phase plan the bays exist for:
        ///   0: NS through+right    1: NS lefts (protected)
        ///   2: EW through+right    3: EW lefts (protected)
        /// PermissiveLefts — 2-phase; bay lefts run permissive against opposing
        /// through (gap acceptance), the pre-bay behavior but from a bay.
        /// Movement indices are derived from the real geometry via a throwaway
        /// RoadNetwork build, so phase data can never drift from the network.
        /// </summary>
        public static List<PhaseDef> BuildPhases(NetworkDef def, PhaseScheme scheme)
        {
            var net = RoadNetwork.Build(def);
            var node = net.NodeById(Center);

            bool IsNs(Movement m)
            {
                var from = net.NodeById(net.LinkById(m.InLink).From);
                return Math.Abs(from.Y) > Math.Abs(from.X);
            }

            var phases = new List<PhaseDef>();
            if (scheme == PhaseScheme.ProtectedLefts)
            {
                for (int axis = 0; axis < 2; axis++)
                {
                    var thr = new PhaseDef(); var left = new PhaseDef();
                    foreach (var m in node.Movements)
                    {
                        if (IsNs(m) != (axis == 0)) continue;
                        if (m.Turn == TurnMask.Left) left.movements.Add(m.Index);
                        else thr.movements.Add(m.Index);
                    }
                    phases.Add(thr); phases.Add(left);
                }
            }
            else
            {
                for (int axis = 0; axis < 2; axis++)
                {
                    var p = new PhaseDef();
                    foreach (var m in node.Movements)
                    {
                        if (IsNs(m) != (axis == 0)) continue;
                        p.movements.Add(m.Index);
                        if (m.Turn == TurnMask.Left) p.permissive.Add(m.Index);
                    }
                    phases.Add(p);
                }
            }
            net.ValidatePhases(node, phases);   // fail at build, not at load
            return phases;
        }
    }
}
