using System.Runtime.CompilerServices;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer
{

    public readonly record struct Stat(NGram ngram, ulong count);
    public class StatsAnalyzer
    {

        public double Error => _sketchBuffer.Sum(sketch => sketch?.errorPerItem ?? 0);
        public double Confidence => _sketchBuffer[0].confidence;

        public IEnumerable<Stat> Stats
        {
            get
            {
                foreach ((ulong topN, ulong count) in this._topNQueue.UnorderedItems)
                {
                    yield return new Stat(new NGram(topN), count);
                }
            }
        }

        public IEnumerable<Stat> StatsAscending
        {
            get
            {
                var queue = new PriorityQueue<ulong, ulong>(_topN);
                while (queue.Count > 0)
                {
                    queue.TryDequeue(out ulong ngram,out ulong count);
                    yield return new Stat(new NGram(ngram), count);
                }
            }
        }


        public NGram Ngram => _ngram;

        private readonly PriorityQueue<ulong, ulong> _topNQueue;
        private Dictionary<ulong, ulong> _topNMap;

        private CMSketch _sketch;
        public double sketchResetError;
        private CMSketch[] _sketchBuffer;
        private int _sketchBufferPos = 0;
        private int _currentSketch = 0;

        private int _topN;
        private NGram _ngram = new NGram();

        private int _capacity;
        private ulong _minSupport;
        private ulong _max = 1;

        //  public StatsAnalyzer(int topN, int buckets, int numberOfHashFunctions, int capacity, ulong minSupport, int sketchBufferSize = 100, double sketchResetError = 0.001) : this(topN, new CMSketch(numberOfHashFunctions, buckets), capacity, minSupport, sketchBufferSize, sketchResetError)
        //  {
        //  }

        public StatsAnalyzer(StatsAnalyzerConfig config) : this(config.TopN, new CMSketchBuilder().Build(config.Sketch), config.Capacity, config.MinSupport, config.BufferSizeForSketches, config.SketchResetOrReuseThreshold)
        {

        }

        public StatsAnalyzer(int topN, CMSketch sketch, int capacity, ulong minSupport, int sketchBufferSize, double sketchResetError)
        {
            _topN = topN;
            _sketch = sketch;
            this.sketchResetError = sketchResetError;
            _sketchBuffer = new CMSketch[sketchBufferSize];
            _sketchBuffer[0] = sketch;
            _topNQueue = new PriorityQueue<ulong, ulong>(_topN);
            _topNMap = new Dictionary<ulong, ulong>(capacity);
            _capacity = capacity;
            _minSupport = minSupport;
        }

        private void ResetSketchAtError()
        {
            if (_max <= _minSupport || (!((_sketch.errorPerItem / (double)_max) >= sketchResetError))) return;
            if (_sketchBufferPos < (_sketchBuffer.Length - 1))
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

        public void Add(IEnumerable<Instruction> instructions)
        {

            ResetSketchAtError();

            foreach (var instruction in instructions)
            {
                _ngram = NGram.ProcessEachSubsequence(_ngram.ShiftAdd(instruction), ProcessNGram);
            }
            _ngram = _ngram.ShiftAdd(NGram.RESET);
            ProcessTopN();
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(Instruction instruction)
        {
            ResetSketchAtError();
            _ngram = NGram.ProcessEachSubsequence(_ngram.ShiftAdd(instruction), ProcessNGram);
            ProcessTopN();
        }


        private void ProcessNGram(NGram ngram)
        {
            _sketchBuffer[_currentSketch].Update(ngram.ulong0);
            var count = QueryAllSketches(ngram.ulong0);
            if (count < _minSupport) return;
            _topNMap[ngram.ulong0] = count;
        }

        private ulong QueryAllSketches(ulong ngram)
        {
            var count = 0UL;
            for (int i = 0; i <= _sketchBufferPos; i++)
                count += _sketchBuffer[i].Query(ngram);
            return count;
        }

        private void ProcessTopN()
        {
            _topNQueue.Clear();
            foreach (var kvp in _topNMap)
            {
                // if count is less than minSupport remove from topNMap and  continue;
                if (kvp.Value < _minSupport)
                {
                    _topNMap.Remove(kvp.Key);
                    continue;
                }

                _max = Math.Max(_max, kvp.Value);

                if (_topNQueue.Count < _topN)
                    _topNQueue.Enqueue(kvp.Key, kvp.Value);

                if (_topNQueue.Count < _topN) continue;
                _topNQueue.DequeueEnqueue(kvp.Key, kvp.Value);
                //Queue has filled up, we update min support to filter out lower count updates
                _topNQueue.TryPeek(out ulong _, out _minSupport);
            }

        }


    }

}
