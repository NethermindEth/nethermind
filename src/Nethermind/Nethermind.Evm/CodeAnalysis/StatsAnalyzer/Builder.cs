
using System;
using Nethermind.Evm.Config;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{
    public abstract class Builder
    {
        public abstract StatsAnalyzer Build();
        public abstract StatsAnalyzer Build(IVMConfig config);
        public abstract Builder SetSketch(CMSketch sketch);
        public abstract Builder SetTopN(int topN);
        public abstract Builder SetMinSupport(ulong support);
        public abstract Builder SetCapacity(int capacity);
        public abstract Builder SetBufferSizeForSketches(int size);
        public abstract Builder SetSketchResetOrReuseThreshold(double error);
    }

    public class StatsAnalyzerBuilder : Builder
    {

        private int? _bufferSizeForSketches = null;
        private int? _capacity = null;
        private CMSketch? _sketch = null;
        private double? _sketchResetOrReuseThreshold = null;
        private int? _topN = null;
        private ulong? _minSupport = null;


        public override StatsAnalyzer Build(IVMConfig config)
        {
            throw new NotImplementedException();
        }

        public override StatsAnalyzer Build()
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

            return new StatsAnalyzer(_topN.Value, _sketch, _capacity.Value, _minSupport.Value, _bufferSizeForSketches.Value, _sketchResetOrReuseThreshold.Value);
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

        public override Builder SetSketch(CMSketch sketch)
        {
            _sketch = sketch;
            return this;

        }
    }

}
