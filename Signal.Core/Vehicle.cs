using System;
using System.Collections.Generic;

namespace Signal.Core
{
    public static class SimConfig
    {
        public const float DT = 0.1f;          // fixed sim timestep (s). Never vary.
        public const float JamGap = 2.0f;      // standstill gap between vehicles (m)
        public const float QueueSpeed = 2.0f;  // below this a vehicle counts as queued (m/s)
        public const float WaitSpeed = 3.0f;   // below this, wait time accrues (m/s)
    }

    public sealed class Vehicle
    {
        public long Id;
        public int[] Route;        // link ids, origin->dest
        public int RouteIdx;       // index of current link in Route
        public float Pos;          // distance from link start (m)
        public float Speed;        // m/s
        public float Length = 4.5f;
        public float Wait;         // cumulative seconds below WaitSpeed
        public float SpawnTime;
        public bool HasStopped;    // has come to a full stop at the current stop line (stop-sign logic)

        public int CurrentLink => Route[RouteIdx];
        public bool OnLastLink => RouteIdx == Route.Length - 1;
        public int NextLink => OnLastLink ? -1 : Route[RouteIdx + 1];
    }

    /// <summary>
    /// Intelligent Driver Model. One function; leaders and stop lines are both
    /// expressed as (gap, closing speed) obstacles, so red lights, stop signs,
    /// yield refusals, and spillback all reuse the same braking math.
    /// </summary>
    public static class Idm
    {
        public const float MaxAccel = 2.0f;    // a (m/s^2)
        public const float ComfortDecel = 3.0f;// b (m/s^2)
        public const float HardDecel = 6.0f;   // physical brake limit
        public const float MinGap = 2.0f;      // s0 (m)
        public const float Headway = 1.2f;     // T (s)

        /// <param name="v">current speed</param>
        /// <param name="vDesired">free-flow target (link speed limit)</param>
        /// <param name="gap">bumper-to-obstacle distance (m); use float.MaxValue for none</param>
        /// <param name="closingSpeed">v - vLeader (use v against a fixed stop line)</param>
        public static float Acceleration(float v, float vDesired, float gap, float closingSpeed)
        {
            float free = 1f - Pow4(v / Math.Max(vDesired, 0.1f));
            if (gap >= float.MaxValue * 0.5f)
                return Clamp(MaxAccel * free, -HardDecel, MaxAccel);

            gap = Math.Max(gap, 0.1f);
            float sStar = MinGap + Math.Max(0f, v * Headway + v * closingSpeed / (2f * Sqrt(MaxAccel * ComfortDecel)));
            float a = MaxAccel * (free - (sStar / gap) * (sStar / gap));
            return Clamp(a, -HardDecel, MaxAccel);
        }

        private static float Pow4(float x) { float x2 = x * x; return x2 * x2; }
        private static float Sqrt(float x) => (float)Math.Sqrt(x);
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
