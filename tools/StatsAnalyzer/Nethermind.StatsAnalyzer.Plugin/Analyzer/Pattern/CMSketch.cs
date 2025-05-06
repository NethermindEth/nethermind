using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;

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
        var sketch = new CmSketch(_sketchNumberOfHashFunctions.Value, _sketchBuckets.Value);
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


    private CmSketch(ulong[] sketch, long[] seeds, int hashFunctions, int buckets) : this(hashFunctions, buckets)
    {
        if (sketch.Length != buckets * hashFunctions)
            throw new ArgumentException(
                $"Invalid sketch array length, expected {buckets * hashFunctions} found: {sketch.Length}.");
        _sketch = sketch;
        _seeds = seeds;
    }

    public double ErrorPerItem => Error * _seen;

    public void Update(ulong item)
    {
        _seen++;
        for (var hasher = 0; hasher < numberOfhashFunctions; hasher++)
            Increment(item, hasher);
    }


    private static long[] GenerateSeed(int numberOfhashFunctions)
    {
        var seeds = new long[numberOfhashFunctions];
        var rand = new Random();
        for (var i = 0; i < numberOfhashFunctions; i++) seeds[i] = rand.NextInt64(long.MinValue, long.MaxValue);

        return seeds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong Increment(ulong item, int hasher)
    {
        return Interlocked.Increment(
            ref _sketch[(ulong)(hasher + 1) * (ComputeHash(item, hasher) % (ulong)numberOfBuckets)]);
    }

    public ulong Query(ulong item)
    {
        var minCount = ulong.MaxValue;
        for (var hasher = 0; hasher < numberOfhashFunctions; hasher++)
            minCount = Math.Min(minCount,
                _sketch[(ulong)(hasher + 1) * (ComputeHash(item, hasher) % (ulong)numberOfBuckets)]);
        return minCount;
    }


    public ulong UpdateAndQuery(ulong item)
    {
        _seen++;
        var minCount = ulong.MaxValue;
        for (var hasher = 0; hasher < numberOfhashFunctions; hasher++)
            minCount = Math.Min(minCount, Increment(item, hasher));
        return minCount;
    }

    public CmSketch Reset()
    {
        var cms = new CmSketch((ulong[])_sketch.Clone(), (long[])_seeds.Clone(), numberOfhashFunctions, numberOfBuckets);
        _sketch = new ulong[numberOfBuckets * numberOfhashFunctions];
        _seeds = GenerateSeed(numberOfhashFunctions);
        return cms;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ComputeHash(ulong value, int hasher)
    {
        // Ideally more families of hash functions should go here:
        switch (hasher)
        {
            default:
                return Fnv1A64(value, _seeds[hasher]);
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fnv1A64(ulong value, long seed)
    {
        // http://isthe.com/chongo/tech/comp/fnv/#FNV-1a
        const ulong fnvOffsetBasis64 = 14695981039346656037; //64-bit
        const ulong fnvPrime64 = 1099511628211; //64-bit

        var hash = (fnvOffsetBasis64 ^ (byte)(value & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)(seed & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((value >> 8) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((seed >> 8) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((value >> 16) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((seed >> 16) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((value >> 24) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((seed >> 24) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((value >> 32) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((seed >> 32) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((value >> 40) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((seed >> 40) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((value >> 48) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((seed >> 48) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((value >> 56) & 0xFF)) * fnvPrime64;
        hash = (hash ^ (byte)((seed >> 56) & 0xFF)) * fnvPrime64;

        return hash;
    }
}
