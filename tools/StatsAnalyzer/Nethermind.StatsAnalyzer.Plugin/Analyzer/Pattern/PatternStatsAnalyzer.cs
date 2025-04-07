using System.Runtime.CompilerServices;
using Nethermind.Evm;
using Nethermind.PatternAnalyzer.Plugin.Types;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;

public readonly record struct Stat(NGram Ngram, ulong Count);

public class PatternStatsAnalyzer : TopNAnalyzer<Instruction, ulong, Stat>
{
    private readonly int _currentSketch = 0;

    private readonly CmSketch _sketch;
    private readonly CmSketch[] _sketchBuffer;

    private int _currentSketchBufferSize;
    private NGram _ngram;
    private double _sketchResetError;

    public PatternStatsAnalyzer(PatternStatsAnalyzerConfig config) : this(config.TopN,
        new CmSketchBuilder().Build(config.Sketch),
        config.Capacity, config.MinSupport, config.BufferSizeForSketches, config.SketchResetOrReuseThreshold)
    {
    }

    public PatternStatsAnalyzer(int topN, CmSketch sketch, int capacity, ulong minSupport, int sketchBufferSize,
        double sketchResetError) : base(topN)
    {
        _sketch = sketch;
        _sketchResetError = sketchResetError;
        _sketchBuffer = new CmSketch[sketchBufferSize];
        _sketchBuffer[0] = sketch;
        MinSupport = minSupport;
    }

    public double Error => _sketchBuffer.Sum(sketch => sketch?.ErrorPerItem ?? 0);
    public double Confidence => _sketchBuffer[0].Confidence;


    private void ResetSketchAtError()
    {
        if (Max <= MinSupport || !(_sketch.ErrorPerItem / Max >= _sketchResetError)) return;
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

    public override unsafe void Add(IEnumerable<Instruction> instructions)
    {
        lock (LockObj)
        {
            ResetSketchAtError();

            TopNQueue.Clear();
            foreach (var instruction in instructions)
            {
                _ngram = _ngram.ShiftAdd(instruction);
                delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>,
                    PriorityQueue<ulong, ulong>, ulong> ptr = &ProcessNGram;
                Max = NGram.ProcessEachSubsequence(_ngram, ptr, _currentSketch, _currentSketchBufferSize, MinSupport,
                    Max, TopN, _sketchBuffer,
                    TopNMap, TopNQueue);
            }

            _ngram = _ngram.ShiftAdd(NGram.Reset);
            ProcessTopN();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override unsafe void Add(Instruction instruction)
    {
        lock (LockObj)
        {
            ResetSketchAtError();
            _ngram = _ngram.ShiftAdd(instruction);
            delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>,
                PriorityQueue<ulong, ulong>, ulong> ptr = &ProcessNGram;
            Max = NGram.ProcessEachSubsequence(_ngram, ptr, _currentSketch, _currentSketchBufferSize, MinSupport,
                Max, TopN, _sketchBuffer,
                TopNMap, TopNQueue);
            TopNQueue.TryPeek(out _, out var min);
            MinSupport = Math.Max(min, MinSupport);
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


    public override IEnumerable<Stat> Stats(SortOrder order)
    {
        LockObj.Enter();
        try
        {
            switch (order)
            {
                case SortOrder.Unordered:
                    foreach (var (ngram, count) in TopNQueue.UnorderedItems)
                        yield return new Stat(new NGram(ngram), count);
                    break;
                case SortOrder.Ascending:
                    var queue = new PriorityQueue<ulong, ulong>(TopN);
                    while (queue.Count > 0)
                        if (queue.TryDequeue(out var ngram, out var count))
                            yield return new Stat(new NGram(ngram), count);
                    break;
                case SortOrder.Descending:
                    var queueDecending =
                        new PriorityQueue<ulong, ulong>(TopN, Comparer<ulong>.Create((x, y) => y.CompareTo(x)));
                    foreach (var (ngram, count) in TopNQueue.UnorderedItems) queueDecending.Enqueue(ngram, count);
                    while (queueDecending.Count > 0)
                        if (queueDecending.TryDequeue(out var ngram, out var count))
                            yield return new Stat(new NGram(ngram), count);
                    break;
            }
        }
        finally { LockObj.Exit(); }
    }
}
