using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{

    public class StatsAnalyzer
    {

        public NGrams NGrams => _ngrams;

        public readonly PriorityQueue<ulong, ulong> topNQueue;
        public readonly Dictionary<ulong, ulong> topNMap;

        private CMSketch _sketch;

        private int _topN;
        private NGrams _ngrams = new NGrams(NGrams.NULL);

        private int _capacity;
        private ulong _minSupport;
        private ulong _recentCount;

        public StatsAnalyzer(int topN, int buckets, int numberOfHashFunctions, int capacity, uint minSupport)
        {
            _topN = topN;
            _sketch = new CMSketch(numberOfHashFunctions, buckets);
            topNMap = new Dictionary<ulong, ulong>(capacity);
            topNQueue = new PriorityQueue<ulong, ulong>(_topN);
            _capacity = capacity;
            _minSupport = minSupport;
        }

        public void Add(IEnumerable<Instruction> instructions)
        {
            foreach (Instruction instruction in instructions)
                Add(instruction);
        }

        public void Add(Instruction instruction)
        {
            _ngrams = _ngrams.ShiftAdd(instruction);
            foreach (ulong ngram in _ngrams)
                ProcessNGram(ngram);
        }

        private void ProcessNGram(ulong ngram)
        {
            _recentCount = _sketch.UpdateAndQuery(ngram);

            // if recentCount is less than minSupport  return early;
            if (_recentCount < _minSupport) return;

            // if recentCount is greater than minSupport  and enqueued, we update early and return
            if (topNMap.ContainsKey(ngram))
                topNMap[ngram] = _recentCount;

            Debug.Assert(topNMap.Count <= _capacity,
                    $"topNMap had count {topNMap.Count} that breached capacity of {_capacity}");

            // if recentCount is greater than minSupport  and not enqueued, we add
            if (topNQueue.Count >= _topN)
            {
                while (topNQueue.TryDequeue(out ulong lowestQueuedNGram, out _))
                {
                    // if lowest is greater than this we break out of the loop;
                    if (topNMap[lowestQueuedNGram] >= _recentCount) break;
                    if (topNQueue.TryPeek(out ulong nextLowestQueuedNGram, out ulong nextLowestQueuedNGramCount))
                    {
                        //if the lowest is greater than next lowest, our queue is stale we refresh;
                        if (topNMap[lowestQueuedNGram] > topNMap[nextLowestQueuedNGram])
                        {
                            topNQueue.TryDequeue(out nextLowestQueuedNGram, out nextLowestQueuedNGramCount);
                            topNQueue.Enqueue(lowestQueuedNGram, topNMap[lowestQueuedNGram]);
                            topNQueue.Enqueue(nextLowestQueuedNGram, topNMap[nextLowestQueuedNGram]);
                        } // if lowest is stale we re-queue it with its recent count
                        else if (topNMap[lowestQueuedNGram] >= _recentCount) topNQueue.DequeueEnqueue(lowestQueuedNGram, topNMap[lowestQueuedNGram]);
                        else
                        {
                            // this ngram is greater than the lowest, we remove the lowest and add this ngram
                            topNMap.Remove(lowestQueuedNGram);
                            topNQueue.Enqueue(ngram, _recentCount);
                            break;
                        }

                    }
                }

                //Queue has filled up, we update min support to filter out lower count updates
                topNQueue.TryPeek(out _, out ulong lowestQueuedNGramCount);
                _minSupport = lowestQueuedNGramCount;

            }
            else
            {
                topNMap.TryAdd(ngram, _recentCount);
                topNQueue.Enqueue(ngram, _recentCount); // we haven't seen it and we have capacity  so we enqueue
            }

        }
    }

}
