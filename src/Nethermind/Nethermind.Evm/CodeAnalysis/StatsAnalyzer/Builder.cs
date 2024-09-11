
using System;
using Nethermind.Evm.Config;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{
    public abstract class Builder
    {
        public abstract StatsAnalyzer Build();
        public abstract StatsAnalyzer Build(IVMConfig config);
        public abstract Builder SetTopN(int topN);
        public abstract Builder SetMinSupport(ulong support);
        public abstract Builder SetCapacity(int capacity);
        public abstract Builder SetBufferSizeForSketches(int size);
        public abstract Builder SetSketchBuckets(int buckets);
        public abstract Builder SetSketchNumberOfHashFunctions(int numberOfHashFunctions);
        public abstract Builder SetSketchError(double error);
        public abstract Builder SetSketchErrorProbaility(double probability);
        public abstract Builder SetSketchResetOrReuseThreshold(double error);
    }

    public class StatsAnalyzerBuilder : Builder
    {

        private int? _bufferSizeForSketches = null;
        private int? _capacity = null;
        private int? _sketchBuckets = null;
        private double? _sketchError = null;
        private int? _sketchNumberOfHashFunctions = null;
        private double? _sketchProbabilityOfError = null;
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

            return new StatsAnalyzer(_topN.Value, BuildSketch(), _capacity.Value, _minSupport.Value, _bufferSizeForSketches.Value, _sketchResetOrReuseThreshold.Value);
        }

        private CMSketch BuildSketch()
        {
            if (!((_sketchBuckets.HasValue && _sketchNumberOfHashFunctions.HasValue) || (_sketchProbabilityOfError.HasValue && _sketchError.HasValue)))
                throw new InvalidOperationException("Either sketch buckets and hash functions must be set or error and probability of error must be set.");
            if ((_sketchBuckets.HasValue && _sketchNumberOfHashFunctions.HasValue))
                return new CMSketch(_sketchBuckets.Value, _sketchNumberOfHashFunctions.Value);
            if (_sketchProbabilityOfError.HasValue && _sketchError.HasValue)
                return new CMSketch(_sketchError.Value, _sketchProbabilityOfError.Value);

            throw new InvalidOperationException("Either sketch buckets and hash functions must be set or error and probability of error must be set.");
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

        public override Builder SetSketchBuckets(int buckets)
        {
            if (_sketchProbabilityOfError.HasValue)
                throw new InvalidOperationException("Sketch number of hash functions cannot be set since probability of error is already set");
            if (_sketchError.HasValue)
                throw new InvalidOperationException("Sketch number of hash functions cannot be set since error is already set");
            _sketchBuckets = buckets;
            return this;
        }

        public override Builder SetSketchError(double error)
        {

            if (_sketchNumberOfHashFunctions.HasValue)
                throw new InvalidOperationException("Sketch Number Of Hash Functions must not be set when using error");
            if (_sketchBuckets.HasValue)
                throw new InvalidOperationException("Sketch buckets must not be set when using error");
            _sketchError = error;
            return this;
        }

        public override Builder SetSketchNumberOfHashFunctions(int numberOfHashFunctions)
        {
            if (_sketchProbabilityOfError.HasValue)
                throw new InvalidOperationException("Sketch number of hash functions cannot be set since probability of error is already set");
            if (_sketchError.HasValue)
                throw new InvalidOperationException("Sketch number of hash functions cannot be set since error is already set");
            _sketchNumberOfHashFunctions = numberOfHashFunctions;
            return this;
        }

        public override Builder SetSketchErrorProbaility(double probability)
        {
            if (!_sketchNumberOfHashFunctions.HasValue)
                throw new InvalidOperationException("Sketch Number Of Hash Functions must not be set when using probability of error");
            if (!_sketchBuckets.HasValue)
                throw new InvalidOperationException("Sketch buckets must not be set when using probability of error");
            _sketchProbabilityOfError = probability;
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


    }

}
