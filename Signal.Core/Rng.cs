using System;

namespace Signal.Core
{
    /// <summary>
    /// Deterministic xoshiro256** RNG. Never use UnityEngine.Random or System.Random
    /// in Core: this must produce identical sequences on every platform and runtime.
    /// </summary>
    public sealed class Rng
    {
        private ulong _s0, _s1, _s2, _s3;

        public Rng(ulong seed)
        {
            // SplitMix64 to expand the seed into 4 non-zero state words.
            ulong z = seed;
            ulong Next()
            {
                z += 0x9E3779B97F4A7C15UL;
                ulong x = z;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                return x ^ (x >> 31);
            }
            _s0 = Next(); _s1 = Next(); _s2 = Next(); _s3 = Next();
            if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 1;
        }

        private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

        public ulong NextULong()
        {
            ulong result = Rotl(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3; _s2 ^= t;
            _s3 = Rotl(_s3, 45);
            return result;
        }

        /// <summary>Uniform double in [0,1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public double Range(double min, double max) => min + NextDouble() * (max - min);

        public int RangeInt(int minInclusive, int maxExclusive)
            => minInclusive + (int)(NextULong() % (ulong)(maxExclusive - minInclusive));

        /// <summary>Exponential inter-arrival sample for a Poisson process.</summary>
        public double NextExponential(double ratePerSec)
            => -Math.Log(1.0 - NextDouble()) / ratePerSec;
    }
}
