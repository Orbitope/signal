using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// The player's signal policy. MaxPressure runs the light on its own; a tap
    /// overrides it with a requested phase for HoldSeconds of sim time, then
    /// hands control back. This is what makes a 25-signal map playable: the
    /// player intervenes where it matters and the AI keeps everything else
    /// moving. The ghost runs plain MaxPressure at the same decision cadence,
    /// so the race measures exactly the value of the player's taps.
    /// </summary>
    public sealed class TapOverridePolicy : ISignalPolicy
    {
        public float HoldSeconds = 12f;

        private readonly MaxPressurePolicy _auto = new MaxPressurePolicy();
        private int _phase = -1;
        private float _until = -1f;

        public void Request(int phase, float now) { _phase = phase; _until = now + HoldSeconds; }
        public void Clear() { _phase = -1; _until = -1f; }

        public bool IsOverriding(float now) => _phase >= 0 && now < _until;
        public float Remaining(float now) => IsOverriding(now) ? _until - now : 0f;
        public int OverridePhase => _phase;

        public int SelectPhase(Simulation sim, Node node, SignalController ctl)
            => IsOverriding(sim.Time) ? _phase : _auto.SelectPhase(sim, node, ctl);
    }
}
