using System.Runtime.CompilerServices;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer;

public readonly record struct Stat(NGram Ngram, ulong Count);

public class StatsAnalyzer
{
    private readonly int _currentSketch = 0;
    private readonly object _lock = new();

    private readonly CmSketch _sketch;
    private readonly CmSketch[] _sketchBuffer;

    private readonly int _topN;
    private readonly Dictionary<ulong, ulong> _topNMap;
    private readonly PriorityQueue<ulong, ulong> _topNQueue;
    private int _currentSketchBufferSize;
    private ulong _max = 1;
    private ulong _minSupport;
    private NGram _ngram;
    private double _sketchResetError;

    public StatsAnalyzer(StatsAnalyzerConfig config) : this(config.TopN, new CmSketchBuilder().Build(config.Sketch),
        config.Capacity, config.MinSupport, config.BufferSizeForSketches, config.SketchResetOrReuseThreshold)
    {
    }

    public StatsAnalyzer(int topN, CmSketch sketch, int capacity, ulong minSupport, int sketchBufferSize,
        double sketchResetError)
    {
        _topN = topN;
        _sketch = sketch;
        _sketchResetError = sketchResetError;
        _sketchBuffer = new CmSketch[sketchBufferSize];
        _sketchBuffer[0] = sketch;
        _topNQueue = new PriorityQueue<ulong, ulong>(_topN);
        _topNMap = new Dictionary<ulong, ulong>(capacity);
        _minSupport = minSupport;
    }

    public double Error => _sketchBuffer.Sum(sketch => sketch?.ErrorPerItem ?? 0);
    public double Confidence => _sketchBuffer[0].Confidence;

    public IEnumerable<Stat> Stats
    {
        get
        {
            lock (_lock)
            {
                foreach (var (topN, count) in _topNQueue.UnorderedItems) yield return new Stat(new NGram(topN), count);
            }
        }
    }

    public IEnumerable<Stat> StatsAscending
    {
        get
        {
            lock (_lock)
            {
                var queue = new PriorityQueue<ulong, ulong>(_topN);
                while (queue.Count > 0)
                {
                    queue.TryDequeue(out var ngram, out var count);
                    yield return new Stat(new NGram(ngram), count);
                }
            }
        }
    }


    private void ResetSketchAtError()
    {
        if (_max <= _minSupport || !(_sketch.ErrorPerItem / _max >= _sketchResetError)) return;
        if (_currentSketchBufferSize < _sketchBuffer.Length - 1)
        {
            ++_currentSketchBufferSize;
            _sketchBuffer[_currentSketchBufferSize] = _sketchBuffer[_currentSketch].Reset();
        }
        else
        {
            // buffer is full we reuse sketches
            _currentSketchBufferSize = (_currentSketchBufferSize + 1) % _sketchBuffer.Length;
            _sketchResetError *= 2; // double the error
        }
    }

    public unsafe void Add(IEnumerable<Instruction> instructions)
    {
        lock (_lock)
        {
            ResetSketchAtError();

            _topNQueue.Clear();
            foreach (var instruction in instructions)
            {
                _ngram = _ngram.ShiftAdd(instruction);
                delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>,
                    PriorityQueue<ulong, ulong>, ulong> ptr = &ProcessNGram;
                _max = NGram.ProcessEachSubsequence(_ngram, ptr, _currentSketch, _currentSketchBufferSize, _minSupport,
                    _max, _topN, _sketchBuffer,
                    _topNMap, _topNQueue);
            }

            _ngram = _ngram.ShiftAdd(NGram.Reset);
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
            delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>,
                PriorityQueue<ulong, ulong>, ulong> ptr = &ProcessNGram;
            _max = NGram.ProcessEachSubsequence(_ngram, ptr, _currentSketch, _currentSketchBufferSize, _minSupport,
                _max, _topN, _sketchBuffer,
                _topNMap, _topNQueue);
            _topNQueue.TryPeek(out _, out var min);
            _minSupport = Math.Max(min, _minSupport);
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ProcessNGram(ulong ngram, int currentSketchPos, int bufferSize,
        ulong minSupport, ulong max, int topN, CmSketch[] sketchBuffer, Dictionary<ulong, ulong> topNMap,
        PriorityQueue<ulong, ulong> topNQueue)
    {
        sketchBuffer[currentSketchPos].Update(ngram);
        var count = QueryAllSketches(bufferSize, sketchBuffer, ngram);
        if (count < minSupport)
        {
            topNMap.Remove(ngram);
        }
        else
        {
            topNMap[ngram] = count;
            return Math.Max(max, count);
        }

        return max;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong QueryAllSketches(int bufferSize, CmSketch[] sketchBuffer, ulong ngram)
    {
        var count = 0UL;
        for (var i = 0; i <= bufferSize; i++)
            count += sketchBuffer[i].Query(ngram);
        return count;
    }


    private void ProcessTopN()
    {
        _topNQueue.Clear();

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
