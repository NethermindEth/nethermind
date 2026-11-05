using System.Runtime.CompilerServices;
using Nethermind.Evm;
using Nethermind.StatsAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer.Pattern;

public readonly record struct PatternStat(NGram Ngram, ulong Count);

public class PatternStatsAnalyzer : TopNAnalyzer<Instruction, ulong, PatternStat>
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
            _currentSketchBufferSize = (_currentSketchBufferSize + 1) % _sketchBuffer.Length;
            _sketchResetError *= 2;
        }
    }

    public override unsafe void Add(IEnumerable<Instruction> instructions)
    {
        ResetSketchAtError();

        TopNQueue.Clear();
        foreach (Instruction instruction in instructions)
        {
            _ngram = _ngram.ShiftAdd(instruction);
            delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>,
                PriorityQueue<ulong, ulong>, ulong> ptr = &ProcessNGram;
            Max = NGram.ProcessEachSubsequence(
                    _ngram,
                    ptr,
                    _currentSketch,
                    _currentSketchBufferSize,
                    MinSupport,
                    Max,
                    TopN,
                    _sketchBuffer,
                    TopNMap,
                    TopNQueue);
        }

        _ngram = _ngram.ShiftAdd(NGram.Reset);
        ProcessTopN();
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override unsafe void Add(Instruction instruction)
    {
        ResetSketchAtError();
        _ngram = _ngram.ShiftAdd(instruction);
        delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>,
            PriorityQueue<ulong, ulong>, ulong> ptr = &ProcessNGram;
        Max = NGram.ProcessEachSubsequence(
                    _ngram,
                    ptr,
                    _currentSketch,
                    _currentSketchBufferSize,
                    MinSupport,
                    Max,
                    TopN,
                    _sketchBuffer,
                    TopNMap,
                    TopNQueue);
        TopNQueue.TryPeek(out _, out ulong min);
        MinSupport = Math.Max(min, MinSupport);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ProcessNGram(ulong ngram, int currentSketchPos, int bufferSize,
        ulong minSupport, ulong max, int topN, CmSketch[] sketchBuffer, Dictionary<ulong, ulong> topNMap,
        PriorityQueue<ulong, ulong> topNQueue)
    {
        sketchBuffer[currentSketchPos].Update(ngram);
        ulong count = QueryAllSketches(bufferSize, sketchBuffer, ngram);
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
        ulong count = 0UL;
        for (int i = 0; i <= bufferSize; i++)
            count += sketchBuffer[i].Query(ngram);
        return count;
    }


    public override IEnumerable<PatternStat> Stats(SortOrder order)
    {
        switch (order)
        {
            case SortOrder.Unordered:
                foreach ((ulong ngram, ulong count) in TopNQueue.UnorderedItems)
                    yield return new PatternStat(new NGram(ngram), count);
                break;
            case SortOrder.Ascending:
                PriorityQueue<ulong, ulong> queue = new(TopN);
                foreach ((ulong ngram, ulong count) in TopNQueue.UnorderedItems) queue.Enqueue(ngram, count);
                while (queue.TryDequeue(out ulong ngramAsc, out ulong countAsc))
                    yield return new PatternStat(new NGram(ngramAsc), countAsc);
                break;
            case SortOrder.Descending:
                PriorityQueue<ulong, ulong> queueDecending =
                    new(TopN, Comparer<ulong>.Create((x, y) => y.CompareTo(x)));
                foreach ((ulong ngram, ulong count) in TopNQueue.UnorderedItems) queueDecending.Enqueue(ngram, count);
                while (queueDecending.Count > 0)
                    if (queueDecending.TryDequeue(out ulong ngram, out ulong count))
                        yield return new PatternStat(new NGram(ngram), count);
                break;
        }
    }
}
