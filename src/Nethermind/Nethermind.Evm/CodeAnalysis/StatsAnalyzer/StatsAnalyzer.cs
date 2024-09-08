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
        private CMSketch[] _previousSketches;

        private int _topN;
        private NGrams _ngrams = new NGrams(NGrams.NULL);

        private int _capacity;
        private ulong _minSupport;

        public StatsAnalyzer(int topN, int buckets, int numberOfHashFunctions, int capacity, uint minSupport, int sketchBufferSize = 100)
        {
            _topN = topN;
            _sketch = new CMSketch(numberOfHashFunctions, buckets);
            _previousSketches = new CMSketch[sketchBufferSize];
            topNQueue = new PriorityQueue<ulong, ulong>(_topN);
            _capacity = capacity;
            _unique = new HashSet<ulong>(capacity);
            _minSupport = minSupport;
        }

        public void CheckError()
        {

        }
        public void Add(IEnumerable<Instruction> instructions)
        {
            foreach (Instruction instruction in instructions)
                _ngrams = _ngrams.ProcessOneInstruction(instruction, ProcessNGram);
            ProcessTopN();
        }

        public void Add(Instruction instruction)
        {
            _ngrams = _ngrams.ProcessOneInstruction(instruction, ProcessNGram);
            ProcessTopN();
        }


        private void ProcessNGram(ulong ngram)
        {
            var count = _sketch.UpdateAndQuery(ngram);
            // if count is less than minSupport  return early;
            if (count < _minSupport) return;
            _unique.Add(ngram);
        }

        private void ProcessTopN()
        {
            var count = 0UL;
            topNQueue.Clear();
            foreach (ulong _ngram in _unique)
            {
                count = _sketch.Query(_ngram);
                // if count is less than minSupport  continue;
                if (count < _minSupport)
                {
                    _unique.Remove(_ngram);
                    continue;
                }

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
