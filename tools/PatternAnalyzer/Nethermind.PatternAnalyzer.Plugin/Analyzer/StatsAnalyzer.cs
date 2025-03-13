using System.Runtime.CompilerServices;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer;

public readonly record struct Stat(NGram ngram, ulong count);

public class StatsAnalyzer
{
    private readonly object _lock = new();
    private readonly int _currentSketch = 0;

    private readonly CMSketch _sketch;
    private readonly CMSketch[] _sketchBuffer;

    private readonly int _topN;
    private readonly Dictionary<ulong, ulong> _topNMap;
    private readonly PriorityQueue<ulong, ulong> _topNQueue;
    private ulong _max = 1;
    private ulong _minSupport;
    private NGram _ngram;
    private int _sketchBufferPos;
    public double sketchResetError;

    public StatsAnalyzer(StatsAnalyzerConfig config) : this(config.TopN, new CMSketchBuilder().Build(config.Sketch),
        config.Capacity, config.MinSupport, config.BufferSizeForSketches, config.SketchResetOrReuseThreshold)
    {
    }

    public StatsAnalyzer(int topN, CMSketch sketch, int capacity, ulong minSupport, int sketchBufferSize,
        double sketchResetError)
    {
        _topN = topN;
        _sketch = sketch;
        this.sketchResetError = sketchResetError;
        _sketchBuffer = new CMSketch[sketchBufferSize];
        _sketchBuffer[0] = sketch;
        _topNQueue = new PriorityQueue<ulong, ulong>(_topN);
        _topNMap = new Dictionary<ulong, ulong>(capacity);
        _minSupport = minSupport;
    }

    public double Error => _sketchBuffer.Sum(sketch => sketch?.errorPerItem ?? 0);
    public double Confidence => _sketchBuffer[0].confidence;

    public IEnumerable<Stat> Stats
    {
        get
        {
            foreach (var (topN, count) in _topNQueue.UnorderedItems) yield return new Stat(new NGram(topN), count);
        }
    }

    public IEnumerable<Stat> StatsAscending
    {
        get
        {
            var queue = new PriorityQueue<ulong, ulong>(_topN);
            while (queue.Count > 0)
            {
                queue.TryDequeue(out var ngram, out var count);
                yield return new Stat(new NGram(ngram), count);
            }
        }
    }


    private void ResetSketchAtError()
    {
        if (_max <= _minSupport || !(_sketch.errorPerItem / _max >= sketchResetError)) return;
        if (_sketchBufferPos < _sketchBuffer.Length - 1)
        {
            ++_sketchBufferPos;
            _sketchBuffer[_sketchBufferPos] = _sketchBuffer[_currentSketch].Reset();
        }
        else
        {
            // buffer is full we reuse sketches
            _sketchBufferPos = (_sketchBufferPos + 1) % _sketchBuffer.Length;
            sketchResetError *= 2; // double the error
        }
    }

    public unsafe void Add(IEnumerable<Instruction> instructions)
    {
        lock (_lock)
        {
            ResetSketchAtError();
            foreach (var instruction in instructions)
            {
                _ngram = _ngram.ShiftAdd(instruction);
                delegate*<ulong, int, int, CMSketch[], ulong, Dictionary<ulong, ulong>, void> ptr = &ProcessNGram;
                NGram.ProcessEachSubsequence(_ngram, ptr, _currentSketch, _sketchBufferPos, _sketchBuffer, _minSupport,
                    _topNMap);
            }
            _ngram = _ngram.ShiftAdd(NGram.RESET);
            ProcessTopN();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Add(Instruction instruction)
    {
        lock (_lock)
        {
            ResetSketchAtError();
            _ngram = _ngram.ShiftAdd(instruction);
            delegate*<ulong, int, int, CMSketch[], ulong, Dictionary<ulong, ulong>, void> ptr = &ProcessNGram;
            NGram.ProcessEachSubsequence(_ngram, ptr, _currentSketch, _sketchBufferPos, _sketchBuffer, _minSupport,
                _topNMap);
            ProcessTopN();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessNGram(ulong ngram, int currentSketchPos, int bufferSize, CMSketch[] sketchBuffer,
        ulong minSupport, Dictionary<ulong, ulong> topNMap)
    {
        sketchBuffer[currentSketchPos].Update(ngram);
        var count = QueryAllSketches(bufferSize, sketchBuffer, ngram);
        if (count < minSupport) return;
        topNMap[ngram] = count;
    }

    private ulong QueryAllSketches2(ulong ngram)
    {
        var count = 0UL;
        for (var i = 0; i <= _sketchBufferPos; i++)
            count += _sketchBuffer[i].Query(ngram);
        return count;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong QueryAllSketches(int bufferSize, CMSketch[] sketchBuffer, ulong ngram)
    {
        var count = 0UL;
        for (var i = 0; i <= bufferSize; i++)
            count += sketchBuffer[i].Query(ngram);
        return count;
    }

    private void ProcessTopN()
    {
        _topNQueue.Clear();

        var keysToRemove = _topNMap.Where(kvp => kvp.Value < _minSupport).Select(kvp => kvp.Key).ToList();

        foreach (var key in keysToRemove)
        {
            _topNMap.Remove(key);
        }

        foreach (var kvp in _topNMap)
        {
            _max = Math.Max(_max, kvp.Value);

            if (_topNQueue.Count < _topN)
                _topNQueue.Enqueue(kvp.Key, kvp.Value);

            if (_topNQueue.Count < _topN) continue;
            _topNQueue.DequeueEnqueue(kvp.Key, kvp.Value);
            //Queue has filled up, we update min support to filter out lower count updates
            _topNQueue.TryPeek(out _, out _minSupport);
        }
    }
}
