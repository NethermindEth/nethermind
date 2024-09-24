using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{


    public class CMSketchBuilder
    {

        private int? _sketchBuckets = null;
        private double? _sketchError = null;
        private int? _sketchNumberOfHashFunctions = null;
        private double? _confidence = null;

        public CMSketch Build(CMSketchConfig config)
        {
            if (config.Buckets.HasValue && config.MaxError.HasValue)
                throw new InvalidOperationException("Found values for buckets and max error. Only one property out of the two must be not both.");
            if (config.HashFunctions.HasValue && config.MinConfidence.HasValue)
                throw new InvalidOperationException("Found values for hash functions and  min confidence. Only one property out of the two must be not both.");
            if (config.Buckets.HasValue) SetBuckets(config.Buckets.Value);
            if (config.MaxError.HasValue) SetMaxError(config.MaxError.Value);
            if (config.HashFunctions.HasValue) SetHashFunctions(config.HashFunctions.Value);
            if (config.MinConfidence.HasValue) SetMinConfidence(config.MinConfidence.Value);
            return Build();

        }
        public CMSketch Build()
        {
            if ((_sketchBuckets.HasValue && _sketchNumberOfHashFunctions.HasValue))
            {
                CMSketch sketch = new CMSketch(_sketchNumberOfHashFunctions.Value, _sketchBuckets.Value);
                if (_sketchError.HasValue)
                    Debug.Assert(sketch.error <= _sketchError, $" expected sketch error to be initialized to at most {_sketchError} found {sketch.error}");
                if (_confidence.HasValue)
                    Debug.Assert(sketch.confidence >= _confidence, $" expected sketch confidence to be at least {_confidence} found {sketch.confidence}");
                return sketch;
            }
            throw new InvalidOperationException("(buckets or max error) and (hash functions or min confidence) must be set.");
        }

        public CMSketchBuilder SetBuckets(int buckets)
        {
            _sketchBuckets = buckets;
            return this;
        }

        public CMSketchBuilder SetHashFunctions(int numberOfHashFunctions)
        {
            _sketchNumberOfHashFunctions = numberOfHashFunctions;
            return this;
        }

        public CMSketchBuilder SetMinConfidence(double probability)
        {
            _sketchNumberOfHashFunctions = (int)Math.Ceiling(Math.Log2(1.0d / (1.0d - probability)));
            _confidence = probability;
            return this;
        }

        public CMSketchBuilder SetMaxError(double error)
        {
            _sketchError = error;
            _sketchBuckets = (int)Math.Ceiling((2.0d / error));
            return this;
        }

    }



    public class CMSketch
    {

        public double errorPerItem => error * (double)_seen;

        public readonly double error;
        public readonly double confidence;

        private ulong _seen = 0;
        private ulong[] _sketch;
        private Int64[] _seeds;

        public readonly int buckets;
        public readonly int hashFunctions;


        // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= 1 - (2 ^ (-numberOfHashFunctions))
        // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= confidence
        // To maximize accuracy minimize maxError and maximize confidence
        public CMSketch(double maxError, double confidence) : this((int)Math.Ceiling(Math.Log2(1.0d / (1.0d - confidence))), (int)Math.Ceiling((2.0d / maxError)))
        {
            Debug.Assert(error <= maxError, $" expected sketch error to be initialized to at most {maxError} found {error}");
            Debug.Assert(this.confidence >= confidence, $" expected sketch confidence to be at least {confidence} found {confidence}");
        }


        public CMSketch(int numberOfhashFunctions, int numberOfBuckets)
        {
            confidence = 1.0d - Math.Pow(0.5d, numberOfhashFunctions);
            _sketch = new ulong[numberOfhashFunctions * numberOfBuckets];
            buckets = numberOfBuckets;
            error = 2.0d / (double)numberOfBuckets;
            hashFunctions = numberOfhashFunctions;
            _seeds = GenerateSeed(numberOfhashFunctions);
        }


        private CMSketch(ulong[] sketch, Int64[] seeds, int hashFunctions, int buckets) : this(hashFunctions, buckets)
        {
            if (sketch.Length != buckets * hashFunctions)
                throw new ArgumentException($"Invalid sketch array length, expected {buckets * hashFunctions} found: {sketch.Length}.");
            this._sketch = sketch;
            this._seeds = seeds;
        }

        public void Update(ulong item)
        {

            _seen++;
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
            _seen++;
            var minCount = ulong.MaxValue;
            for (int hasher = 0; hasher < hashFunctions; hasher++)
                minCount = Math.Min(minCount, Increment(item, hasher));
            return minCount;
        }

        public CMSketch Reset()
        {
            CMSketch cms = new CMSketch((ulong[])_sketch.Clone(), (Int64[])_seeds.Clone(), hashFunctions, buckets);
            _sketch = new ulong[buckets * hashFunctions];
            _seeds = GenerateSeed(hashFunctions);
            return cms;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ComputeHash(ulong value, int hasher)
        {
            // Ideally more families of hash functions should go here:
            switch (hasher)
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
            hash = (hash ^ (byte)((seed >> 48) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((value >> 56) & 0xFF)) * FNV_PRIME_64;
            hash = (hash ^ (byte)((seed >> 56) & 0xFF)) * FNV_PRIME_64;

            return hash;

        }

    }
}
