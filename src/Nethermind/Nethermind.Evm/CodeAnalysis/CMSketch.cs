
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
        private Int64[] _seeds;
        public readonly int buckets;
        public readonly int hashFunctions;


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
            _seeds = GenerateSeed(numberOfhashFunctions);
        }


        public CMSketch(int numberOfhashFunctions, int numberOfBuckets)
        {
            probabilityOneMinusDelta = 1 - Math.Pow(0.5, numberOfhashFunctions);
            _sketch = new ulong[numberOfBuckets * numberOfhashFunctions];
            buckets = numberOfBuckets;
            error = (double)2.0 / numberOfBuckets;
            hashFunctions = numberOfhashFunctions;
            _seeds = GenerateSeed(numberOfhashFunctions);
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


        private Int64[] GenerateSeed(int numberOfhashFunctions)
        {
            var seeds = new Int64[numberOfhashFunctions];
            Random rand = new Random();
            for (int i = 0; i < numberOfhashFunctions; i++)
            {
                seeds[i] = rand.NextInt64(Int64.MinValue, Int64.MaxValue);
            }
            return seeds;
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
            CMSketch cms = new CMSketch(_sketch, buckets, hashFunctions);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ComputeHash(ulong value, int hasher)
        {
           // Ideally more families of hash functions should go here:
           switch(hasher)
           {
               default:
                   return FNV1a64(value, _seeds[hasher]);
           }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong FNV1a64(ulong value, Int64 seed)
        {

            // http://isthe.com/chongo/tech/comp/fnv/#FNV-1a
            const ulong FNV_OFFSET_BASIS_64 = 14695981039346656037; //64-bit
            const ulong FNV_PRIME_64 = 1099511628211; //64-bit

            var hash = (FNV_OFFSET_BASIS_64 ^ (byte)(value & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)(seed & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 8) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 8) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 16) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 16) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 24) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 24) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 32) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 32) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 40) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 40) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 48) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 58) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 56) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 56) & 0xFF)) * FNV_PRIME_64;

            return hash;

        }

    }
}
