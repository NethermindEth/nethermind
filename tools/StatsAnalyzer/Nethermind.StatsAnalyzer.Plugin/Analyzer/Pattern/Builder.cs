namespace Nethermind.StatsAnalyzer.Plugin.Analyzer.Pattern;

public interface Builder
{
    PatternStatsAnalyzer Build();
    Builder SetSketch(CmSketch sketch);
    Builder SetTopN(int topN);
    Builder SetMinSupport(ulong support);
    Builder SetCapacity(int capacity);
    Builder SetBufferSizeForSketches(int size);
    Builder SetSketchResetOrReuseThreshold(double error);
}

public class StatsAnalyzerBuilder : Builder
{
    private int? _bufferSizeForSketches;
    private int? _capacity;
    private ulong? _minSupport;
    private CmSketch? _sketch;
    private double? _sketchResetOrReuseThreshold;
    private int? _topN;


    public PatternStatsAnalyzer Build()
    {
        Check.ThrowIfNull(_bufferSizeForSketches, nameof(_bufferSizeForSketches));
        Check.ThrowIfNull(_minSupport, nameof(_minSupport));
        Check.ThrowIfNull(_capacity, nameof(_capacity));
        Check.ThrowIfNull(_sketchResetOrReuseThreshold, nameof(_sketchResetOrReuseThreshold));
        Check.ThrowIfNull(_topN, nameof(_topN));
        Check.ThrowIfNull(_sketch, nameof(_sketch));

        return new PatternStatsAnalyzer(_topN!.Value, _sketch!, _capacity!.Value, _minSupport!.Value,
            _bufferSizeForSketches!.Value, _sketchResetOrReuseThreshold!.Value);
    }

    public Builder SetBufferSizeForSketches(int size)
    {
        _bufferSizeForSketches = size;
        return this;
    }

    public Builder SetCapacity(int capacity)
    {
        _capacity = capacity;
        return this;
    }

    public Builder SetMinSupport(ulong support)
    {
        _minSupport = support;
        return this;
    }


    public Builder SetSketchResetOrReuseThreshold(double threshold)
    {
        _sketchResetOrReuseThreshold = threshold;
        return this;
    }

    public Builder SetTopN(int topN)
    {
        _topN = topN;
        return this;
    }

    public Builder SetSketch(CmSketch sketch)
    {
        _sketch = sketch;
        return this;
    }
}
