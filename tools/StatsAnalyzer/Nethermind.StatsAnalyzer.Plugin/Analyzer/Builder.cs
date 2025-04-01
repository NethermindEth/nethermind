namespace Nethermind.StatsAnalyzer.Plugin.Analyzer;

public abstract class Builder
{
    public abstract PatternStatsAnalyzer Build();
    public abstract Builder SetSketch(CmSketch sketch);
    public abstract Builder SetTopN(int topN);
    public abstract Builder SetMinSupport(ulong support);
    public abstract Builder SetCapacity(int capacity);
    public abstract Builder SetBufferSizeForSketches(int size);
    public abstract Builder SetSketchResetOrReuseThreshold(double error);
}

public class StatsAnalyzerBuilder : Builder
{
    private int? _bufferSizeForSketches;
    private int? _capacity;
    private ulong? _minSupport;
    private CmSketch? _sketch;
    private double? _sketchResetOrReuseThreshold;
    private int? _topN;


    public override PatternStatsAnalyzer Build()
    {
        if (!_bufferSizeForSketches.HasValue)
            throw new InvalidOperationException("Buffer size for sketches must be set.");
        if (!_minSupport.HasValue)
            throw new InvalidOperationException("Buffer size for sketches must be set.");
        if (!_capacity.HasValue)
            throw new InvalidOperationException("Capacity must be set.");
        if (!_sketchResetOrReuseThreshold.HasValue)
            throw new InvalidOperationException("Sketch reset or reuse threshold must be set.");
        if (!_topN.HasValue)
            throw new InvalidOperationException("Top N must be set.");
        if (_sketch == null)
            throw new InvalidOperationException("Sketch must be set.");

        return new PatternStatsAnalyzer(_topN.Value, _sketch, _capacity.Value, _minSupport.Value,
            _bufferSizeForSketches.Value, _sketchResetOrReuseThreshold.Value);
    }

    public override Builder SetBufferSizeForSketches(int size)
    {
        _bufferSizeForSketches = size;
        return this;
    }

    public override Builder SetCapacity(int capacity)
    {
        _capacity = capacity;
        return this;
    }

    public override Builder SetMinSupport(ulong support)
    {
        _minSupport = support;
        return this;
    }


    public override Builder SetSketchResetOrReuseThreshold(double threshold)
    {
        _sketchResetOrReuseThreshold = threshold;
        return this;
    }

    public override Builder SetTopN(int topN)
    {
        _topN = topN;
        return this;
    }

    public override Builder SetSketch(CmSketch sketch)
    {
        _sketch = sketch;
        return this;
    }
}
