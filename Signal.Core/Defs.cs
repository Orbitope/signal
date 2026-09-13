using System;
using System.Collections.Generic;

namespace Signal.Core
{
    // ---------------------------------------------------------------------
    // Authored data. Plain public fields so both System.Text.Json
    // (IncludeFields=true, used by Signal.Headless) and Unity's JsonUtility /
    // ScriptableObject serialization can handle them without attributes.
    // Core never performs JSON I/O itself; consumers do.
    // ---------------------------------------------------------------------

    public enum ControlType { Uncontrolled, Signalized, AllWayStop, TwoWayStop, YieldEntry }

    [Serializable] public class NodeDef
    {
        public int id;
        public float x, y;                 // presentation + bearing derivation
        public bool isBoundary;            // spawn/sink node
        public ControlType control = ControlType.Uncontrolled;
        public List<PhaseDef> phases;      // Signalized only
        public List<int> majorInLinks;     // TwoWayStop: in-links that never yield
        public float minGreen = 5f, yellow = 3f, allRed = 1f;   // Signalized
        public float stopServiceTime = 2.0f;                    // AllWayStop
        public float gapThreshold = 4.0f;                       // yield-based controls (sec TTA)
    }

    [Serializable] public class LinkDef
    {
        public int id;
        public int from, to;               // node ids
        public float length;               // meters (authoritative; geometry is cosmetic)
        public float speedLimit = 13.9f;   // m/s (~50 km/h)
        /// <summary>Which turn classes this lane-link may take at its downstream
        /// node. Default All. A left-turn bay is a parallel lane-link with
        /// turns=Left; this is how multi-lane works without lane changing —
        /// lane choice happens at fork nodes, never mid-link.</summary>
        public TurnMask turns = TurnMask.All;
    }

    [Flags] public enum TurnMask { None = 0, Left = 1, Through = 2, Right = 4, All = 7 }

    /// <summary>A phase = set of movement indices (into the node's derived movement list)
    /// that may proceed together. Validated against the conflict matrix at load.</summary>
    [Serializable] public class PhaseDef
    {
        public List<int> movements = new List<int>();
        public List<int> permissive = new List<int>(); // subset of movements: allowed but must gap-accept
    }

    [Serializable] public class NetworkDef
    {
        public List<NodeDef> nodes = new List<NodeDef>();
        public List<LinkDef> links = new List<LinkDef>();
    }

    /// <summary>Piecewise-linear rate curve: vehicles/minute over level time.
    /// Serializable stand-in for UnityEngine.AnimationCurve.</summary>
    [Serializable] public class RateCurve
    {
        public List<float> times = new List<float>();   // seconds, ascending
        public List<float> rates = new List<float>();   // veh/min at each time

        public static RateCurve Constant(float vehPerMin)
            => new RateCurve { times = { 0f }, rates = { vehPerMin } };

        public float Evaluate(float t)
        {
            if (times.Count == 0) return 0f;
            if (t <= times[0]) return rates[0];
            for (int i = 1; i < times.Count; i++)
            {
                if (t <= times[i])
                {
                    float u = (t - times[i - 1]) / (times[i] - times[i - 1]);
                    return rates[i - 1] + u * (rates[i] - rates[i - 1]);
                }
            }
            return rates[rates.Count - 1];
        }
    }

    [Serializable] public class OdFlowDef
    {
        public int origin, dest;           // boundary node ids
        public RateCurve rate = new RateCurve();
    }

    [Serializable] public class DemandDef
    {
        public List<OdFlowDef> flows = new List<OdFlowDef>();
    }

    [Serializable] public class LevelDef
    {
        public string name = "unnamed";
        public NetworkDef network = new NetworkDef();
        public DemandDef demand = new DemandDef();
        public float duration = 300f;      // seconds
    }

    /// <summary>Per-node control-type overrides for benchmarking buildable configurations.</summary>
    [Serializable] public class BuildVariant
    {
        public List<int> nodeIds = new List<int>();
        public List<ControlType> controls = new List<ControlType>();
    }
}
