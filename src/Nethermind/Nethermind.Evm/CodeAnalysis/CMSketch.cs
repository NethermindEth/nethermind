
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Evm.CodeAnalysis
{

    public class CMSketch
    {
        public readonly double error;
        public readonly double probabilityOneMinusDelta;

        private ulong[] _sketch;
        private int[] _seeds;
        public readonly int buckets;
        public readonly int hashFunctions;
        private const ulong FNV_OFFSET_BASIS = 14695981039346656037; //64-bit
        private const ulong FNV_PRIME = 1099511628211; //64-bit


        // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= 1 - (2 ^ (-numberOfHashFunctions))
        // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= oneMinusDelta
        public CMSketch(double e, double oneMinusDelta)
        {

            var numberOfBuckets = (int)Math.Round((2.0 / e));
            probabilityOneMinusDelta = oneMinusDelta;
            double delta = 1 - oneMinusDelta;
            double OneByDelta = 1.0 / delta;
            var numberOfhashFunctions = (int)Math.Round(Math.Log2(OneByDelta)) | 1;
            _sketch = new ulong[numberOfBuckets * numberOfhashFunctions];
            buckets = numberOfBuckets;
            error = (double)2.0 / numberOfBuckets;
            hashFunctions = numberOfhashFunctions;
            _seeds = new int[numberOfhashFunctions];

            Random rand = new Random();
            for (int i = 0; i < numberOfhashFunctions; i++)
            {
                _seeds[i] = rand.Next(int.MinValue, int.MaxValue);
            }
        }


        public CMSketch(int numberOfhashFunctions, int numberOfBuckets)
        {
            probabilityOneMinusDelta = 1 - Math.Pow(0.5, numberOfhashFunctions);
            _sketch = new ulong[numberOfBuckets * numberOfhashFunctions];
            buckets = numberOfBuckets;
            error = (double)2.0 / numberOfBuckets;
            hashFunctions = numberOfhashFunctions;
            _seeds = new int[numberOfhashFunctions];

            Random rand = new Random();
            for (int i = 0; i < numberOfhashFunctions; i++)
            {
                _seeds[i] = rand.Next(int.MinValue, int.MaxValue);
            }
        }

        public CMSketch(ulong[] sketch, int buckets, int hashFunctions) : this(buckets, hashFunctions)
        {
            if (sketch.Length != buckets * hashFunctions)
                  throw new ArgumentException($"Invalid sketch array length, expected {buckets * hashFunctions} found: {sketch.Length}.");
            this._sketch = sketch;
        }

        public void Update(ulong item)
        {
            for (int hasher = 0; hasher < hashFunctions; hasher++)
                Increment(item, hasher);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong Increment(ulong item, int hasher)
        {
            return Interlocked.Increment(ref _sketch[(ulong)(hasher + 1) * (ComputeHash(item, hasher) % (ulong)buckets)]);
        }

        public ulong Query(ulong item)
        {
            var minCount = ulong.MaxValue;
            for (int hasher = 0; hasher < hashFunctions; hasher++)
                minCount = Math.Min(minCount,
                        _sketch[(ulong)(hasher + 1) * (ComputeHash(item, hasher) % (ulong)buckets)]);
            return minCount;
        }


        public ulong UpdateAndQuery(ulong item)
        {
            var minCount = ulong.MaxValue;
            for (int hasher = 0; hasher < hashFunctions; hasher++)
                minCount = Math.Min(minCount, Increment(item, hasher));
            return minCount;
        }

        public CMSketch Reset()
        {
            CMSketch cms = new CMSketch(_sketch,buckets,hashFunctions);
            _sketch = new ulong[buckets * hashFunctions];
            return cms;
        }


        public ulong OverEstimationMagnitude()
        {
            ulong overEstimationMagnitude = 0;
            for (int i = 0; i < buckets; i++)
                overEstimationMagnitude += _sketch[i];
            overEstimationMagnitude = (ulong)Math.Round(error * (double)overEstimationMagnitude);
            return overEstimationMagnitude;
        }

        public ulong ComputeHash(ulong value, int hasher)
        {
            // http://isthe.com/chongo/tech/comp/fnv/#FNV-1a
            // FNV_OFFSET_BASIS_64 = 144066263297769815596495629667062367629
            // FNV_PRIME_64  = 1099511628211
            var hash = (FNV_OFFSET_BASIS ^ (byte)(value & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((value >> 8) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((value >> 16) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((value >> 24) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((value >> 32) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((value >> 40) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((value >> 48) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((value >> 56) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)(_seeds[hasher % hashFunctions] & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((_seeds[hasher % hashFunctions] >> 8) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((_seeds[hasher % hashFunctions] >> 16) & 0xFF)) * FNV_PRIME;
            hash = (hash ^ (byte)((_seeds[hasher % hashFunctions] >> 24) & 0xFF)) * FNV_PRIME;

            return hash;

        }

    }
}
