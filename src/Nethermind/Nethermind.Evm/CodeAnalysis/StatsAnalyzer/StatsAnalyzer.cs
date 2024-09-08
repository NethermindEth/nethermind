using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{

    public class StatsAnalyzer
    {

        public NGrams NGrams => _ngrams;

        public readonly PriorityQueue<ulong, ulong> topNQueue;
        private HashSet<ulong> _unique;

        private CMSketch _sketch;
        public readonly double sketchResetError;
        private CMSketch[] _sketchBuffer;
        private int _sketchBufferPos = -1;

        private int _topN;
        private NGrams _ngrams = new NGrams(NGrams.NULL);

        private int _capacity;
        private ulong _minSupport;
        private ulong _max = 1;

        public StatsAnalyzer(int topN, int buckets, int numberOfHashFunctions, int capacity, uint minSupport, int sketchBufferSize = 100, double sketchResetError = 0.001)
        {
            _topN = topN;
            _sketch = new CMSketch(numberOfHashFunctions, buckets);
            this.sketchResetError = sketchResetError;
            _sketchBuffer = new CMSketch[sketchBufferSize];
            topNQueue = new PriorityQueue<ulong, ulong>(_topN);
            _capacity = capacity;
            _unique = new HashSet<ulong>(capacity);
            _minSupport = minSupport;
        }

        private void ResetSketchAtError()
        {
            if (_sketchBufferPos < (_sketchBuffer.Length - 1) && ((_sketch.errorPerItem / (double)_max) >= sketchResetError))
            {
                ++_sketchBufferPos;
                _sketchBuffer[_sketchBufferPos] = _sketch.Reset();
            }
        }

        public void Add(IEnumerable<Instruction> instructions)
        {
            ResetSketchAtError();
            foreach (Instruction instruction in instructions)
                _ngrams = _ngrams.ProcessOneInstruction(instruction, ProcessNGram);
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
            _sketch.Update(ngram);
            var count = QueryAllSketches(ngram);
            if (count < _minSupport) return;
            _unique.Add(ngram);
        }

        private ulong QueryAllSketches(ulong ngram)
        {
            var count = _sketch.Query(ngram);
            for (int i = 0; i <= _sketchBufferPos; i++)
                count += _sketchBuffer[i].Query(ngram);
            return count;
        }

        private void ProcessTopN()
        {
            var count = 0UL;
            topNQueue.Clear();
            foreach (ulong _ngram in _unique)
            {
                count = QueryAllSketches(_ngram);
                // if count is less than minSupport remove from unique and  continue;
                if (count < _minSupport)
                {
                    _unique.Remove(_ngram);
                    continue;
                }

                _max = Math.Max(_max, count);

                if (topNQueue.Count < _topN)
                    topNQueue.Enqueue(_ngram, count);

                if (topNQueue.Count >= _topN)
                {
                    topNQueue.DequeueEnqueue(_ngram, count);
                    //Queue has filled up, we update min support to filter out lower count updates
                    topNQueue.TryPeek(out ulong _, out _minSupport);
                }
            }

        }


    }

}
