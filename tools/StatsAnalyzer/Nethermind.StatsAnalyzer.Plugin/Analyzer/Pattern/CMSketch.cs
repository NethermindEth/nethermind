using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer.Pattern;

public class CmSketchBuilder
{
    private double? _confidence;
    private int? _sketchBuckets;
    private double? _sketchError;
    private int? _sketchNumberOfHashFunctions;

    public CmSketch Build(CmSketchConfig config)
    {
        if (config.Buckets.HasValue && config.MaxError.HasValue)
            throw new InvalidOperationException(
                "Found values for buckets and max error. Only one property out of the two must be not both.");
        if (config.HashFunctions.HasValue && config.MinConfidence.HasValue)
            throw new InvalidOperationException(
                "Found values for hash functions and  min confidence. Only one property out of the two must be not both.");
        if (config.Buckets.HasValue) SetBuckets(config.Buckets.Value);
        if (config.MaxError.HasValue) SetMaxError(config.MaxError.Value);
        if (config.HashFunctions.HasValue) SetHashFunctions(config.HashFunctions.Value);
        if (config.MinConfidence.HasValue) SetMinConfidence(config.MinConfidence.Value);
        return Build();
    }

    public CmSketch Build()
    {
        if (!_sketchBuckets.HasValue || !_sketchNumberOfHashFunctions.HasValue)
            throw new InvalidOperationException(
                "(buckets and number of hash functions must be set.");
        CmSketch sketch = new(_sketchNumberOfHashFunctions.Value, _sketchBuckets.Value);
        if (_sketchError.HasValue)
            Debug.Assert(sketch.Error <= _sketchError,
                $" expected sketch error to be initialized to at most {_sketchError} found {sketch.Error}");
        if (_confidence.HasValue)
            Debug.Assert(sketch.Confidence >= _confidence,
                $" expected sketch confidence to be at least {_confidence} found {sketch.Confidence}");
        return sketch;

    }

    public CmSketchBuilder SetBuckets(int buckets)
    {
        _sketchBuckets = buckets;
        return this;
    }

    public CmSketchBuilder SetHashFunctions(int numberOfHashFunctions)
    {
        _sketchNumberOfHashFunctions = numberOfHashFunctions;
        return this;
    }

    public CmSketchBuilder SetMinConfidence(double probability)
    {
        _sketchNumberOfHashFunctions = (int)Math.Ceiling(Math.Log2(1.0d / (1.0d - probability)));
        _confidence = probability;
        return this;
    }

    public CmSketchBuilder SetMaxError(double error)
    {
        _sketchError = error;
        _sketchBuckets = (int)Math.Ceiling(2.0d / error);
        return this;
    }
}

public class CmSketch(int numberOfhashFunctions, int numberOfBuckets)
{
    public readonly double Confidence = 1.0d - Math.Pow(0.5d, numberOfhashFunctions);

    public readonly double Error = 2.0d / numberOfBuckets;
    private long[] _seeds = GenerateSeed(numberOfhashFunctions);

    private ulong _seen;
    private ulong[] _sketch = new ulong[numberOfhashFunctions * numberOfBuckets];


    /*
     * Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= 1 - (2 ^ (-numberOfHashFunctions))
     * Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= _confidence
     * To maximize accuracy minimize maxError and maximize confidence */
    public CmSketch(double maxError, double confidence) : this(
        (int)Math.Ceiling(Math.Log2(1.0d / (1.0d - confidence))), (int)Math.Ceiling(2.0d / maxError))
    {
        Debug.Assert(Error <= maxError,
            $" expected sketch error to be initialized to at most {maxError} found {Error}");
        Debug.Assert(Confidence >= confidence,
            $" expected sketch confidence to be at least {confidence} found {confidence}");
    }


    private CmSketch(ulong[] sketch, long[] seeds, ulong seen, int hashFunctions, int buckets) : this(hashFunctions, buckets)
    {
        if (sketch.Length != buckets * hashFunctions)
            throw new ArgumentException(
                $"Invalid sketch array length, expected {buckets * hashFunctions} found: {sketch.Length}.");
        _sketch = sketch;
        _seeds = seeds;
        _seen = seen;
    }

    public double ErrorPerItem => Error * _seen;

    public void Update(ulong item)
    {
        _seen++;
        for (int hasher = 0; hasher < numberOfhashFunctions; hasher++)
            Increment(item, hasher);
    }


    private static long[] GenerateSeed(int numberOfhashFunctions)
    {
        long[] seeds = new long[numberOfhashFunctions];
        Random rand = new();
        for (int i = 0; i < numberOfhashFunctions; i++) seeds[i] = rand.NextInt64(long.MinValue, long.MaxValue);

        return seeds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SketchIndex(ulong item, int hasher) =>
        hasher * numberOfBuckets + (int)(ComputeHash(item, hasher) % (uint)numberOfBuckets);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong Increment(ulong item, int hasher) => Interlocked.Increment(ref _sketch[SketchIndex(item, hasher)]);

    public ulong Query(ulong item)
    {
        ulong minCount = ulong.MaxValue;
        for (int hasher = 0; hasher < numberOfhashFunctions; hasher++)
            minCount = Math.Min(minCount, _sketch[SketchIndex(item, hasher)]);
        return minCount;
    }


    public ulong UpdateAndQuery(ulong item)
    {
        _seen++;
        ulong minCount = ulong.MaxValue;
        for (int hasher = 0; hasher < numberOfhashFunctions; hasher++)
            minCount = Math.Min(minCount, Increment(item, hasher));
        return minCount;
    }

    public CmSketch Reset()
    {
        // Seeds must be cloned, not regenerated: QueryAllSketches sums counts
        // across the buffer and requires all sketches to share the same seeds.
        CmSketch cms = new((ulong[])_sketch.Clone(), (long[])_seeds.Clone(), _seen,
                               numberOfhashFunctions, numberOfBuckets);
        _sketch = new ulong[numberOfBuckets * numberOfhashFunctions];
        _seen = 0;
        return cms;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ComputeHash(ulong value, int hasher)
    {
        switch (hasher)
        {
            default:
                return Fnv1A64(value, _seeds[hasher]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fnv1A(ulong value, long seed, ulong offsetBasis, ulong prime, int sizeInBytes)
    {
        ulong startHash = offsetBasis;
        ulong hash = 0UL; // for 0 size
        for (int i = 0; i < sizeInBytes; i++)
        {
            startHash = (startHash ^ (byte)((value >> (i * 8)) & 0xFF)) * prime;
            startHash = (startHash ^ (byte)((seed >> (i * 8)) & 0xFF)) * prime;
            hash = startHash;
        }
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fnv1A64(ulong value, long seed)
    {
        // http://isthe.com/chongo/tech/comp/fnv/#FNV-1a
        const ulong fnvOffsetBasis64 = 14695981039346656037; //64-bit
        const ulong fnvPrime64 = 1099511628211; //64-bit

        return Fnv1A(value, seed, fnvOffsetBasis64, fnvPrime64, 8);
    }
}
