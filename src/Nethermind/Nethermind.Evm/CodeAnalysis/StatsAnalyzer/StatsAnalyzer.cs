using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{

    public class StatsAnalyzer
    {

        public NGrams ngrams => _ngrams;

        public readonly PriorityQueue<ulong, ulong> topNQueue;
        private Dictionary<ulong, ulong> _topNMap;

        private CMSketch _sketch;
        public double sketchResetError;
        private CMSketch[] _sketchBuffer;
        private int _sketchBufferPos = 0;
        private int _currentSketch = 0;

        private int _topN;
        private NGrams _ngrams = new NGrams();

        private int _capacity;
        private ulong _minSupport;
        private ulong _max = 1;

        public StatsAnalyzer(int topN, int buckets, int numberOfHashFunctions, int capacity, uint minSupport, int sketchBufferSize = 100, double sketchResetError = 0.001) : this(topN, new CMSketch(numberOfHashFunctions, buckets), capacity, minSupport, sketchBufferSize, sketchResetError)
        {
        }

        public StatsAnalyzer(int topN, CMSketch sketch, int capacity, uint minSupport, int sketchBufferSize, double sketchResetError)
        {
            _topN = topN;
            _sketch = sketch;
            this.sketchResetError = sketchResetError;
            _sketchBuffer = new CMSketch[sketchBufferSize];
            _sketchBuffer[0] = sketch;
            topNQueue = new PriorityQueue<ulong, ulong>(_topN);
            _topNMap = new Dictionary<ulong, ulong>(capacity);
            _capacity = capacity;
            _minSupport = minSupport;
        }

        private void ResetSketchAtError()
        {
            if (_max > _minSupport && ((_sketch.errorPerItem / (double)_max) >= sketchResetError))
            {
                if (_sketchBufferPos < (_sketchBuffer.Length - 1))
                {
                     ++_sketchBufferPos;
                    _sketchBuffer[_sketchBufferPos] = _sketch.Reset();
                } else {
                    // buffer is full we reuse sketches
                    _currentSketch = (_currentSketch + 1) % _sketchBuffer.Length;
                    sketchResetError *= 2; // double the error
                }
            }
        }

        public void Add(IEnumerable<Instruction> instructions)
        {

            ResetSketchAtError();
            _ngrams = NGrams.ProcessInstructions(instructions,_ngrams,ProcessNGram).ShiftAdd(NGrams.RESET);
            ProcessTopN();
        }

        public void Add(Instruction instruction)
        {
            ResetSketchAtError();
            _ngrams = _ngrams.ProcessOneInstruction(instruction, ProcessNGram);
            ProcessTopN();
        }


        private void ProcessNGram(ulong ngram)
        {
            _sketchBuffer[_currentSketch].Update(ngram);
            var count = QueryAllSketches(ngram);
            if (count < _minSupport) return;
            _topNMap.Add(ngram, count);
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
            topNQueue.Clear();
            foreach (KeyValuePair<ulong, ulong> kvp in _topNMap)
            {
                // if count is less than minSupport remove from topNMap and  continue;
                if (kvp.Value < _minSupport)
                {
                    _topNMap.Remove(kvp.Key);
                    continue;
                }

                _max = Math.Max(_max, kvp.Value);

                if (topNQueue.Count < _topN)
                    topNQueue.Enqueue(kvp.Key, kvp.Value);

                if (topNQueue.Count >= _topN)
                {
                    topNQueue.DequeueEnqueue(kvp.Key, kvp.Value);
                    //Queue has filled up, we update min support to filter out lower count updates
                    topNQueue.TryPeek(out ulong _, out _minSupport);
                }
            }

        }


    }

}
