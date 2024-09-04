
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis
{

    public class StatsAnalyzer
    {
        public readonly PriorityQueue<ulong, ulong> topNQueue;
        public readonly Dictionary<ulong, ulong> topNMap;

        private CMSketch _sketch;

        private int _topN;
        private ulong _ngram = 0;

        private int _capacity;
        private ulong _minSupport;
        private ulong _recentCount;

        const ulong twogramBitMask = (255UL << 8) | 255UL;
        const ulong threegramBitMask = (255UL << 8 * 2) | twogramBitMask;
        const ulong fourgramBitMask = (255U << 8 * 3) | threegramBitMask;
        const ulong fivegramBitMask = (255UL << 8 * 4) | fourgramBitMask;
        const ulong sixgramBitMask = (255UL << 8 * 5) | fivegramBitMask;
        const ulong sevengramBitMask = (255UL << 8 * 6) | sixgramBitMask;
        public ulong[] ngramBitMaks = [255UL, twogramBitMask, threegramBitMask, fourgramBitMask, fivegramBitMask, sixgramBitMask, sevengramBitMask];
        public static ulong[] byteIndexes = { 255UL, 255UL << 8, 255UL << 16, 255UL << 24, 255UL << 32, 255UL << 40, 255UL << 48, 255UL << 56 };
        public static ulong[] byteIndexShifts = { 0, 8, 16, 24, 32, 40, 48, 56 };

        public StatsAnalyzer(int topN, int buckets, int numberOfHashFunctions, int capacity, uint minSupport)
        {
            _topN = topN;
            _sketch = new CMSketch(numberOfHashFunctions, buckets);
            topNMap = new Dictionary<ulong, ulong>(capacity);
            topNQueue = new PriorityQueue<ulong, ulong>(_topN);
            _capacity = capacity;
            _minSupport = minSupport;
        }

        public bool Add(IEnumerable<Instruction> instructions)
        {
            foreach (Instruction instruction in instructions)
            {
                if (!Add(instruction)) return false;
            }
            return true;
        }

        public bool Add(Instruction instruction)
        {
            if (instruction == Instruction.STOP)
            {
                _ngram = 0;
                return true;
            }

            _ngram = (_ngram << 8) | (byte)instruction;

            for (int i = 1; i < 7; i++)
                if (byteIndexes[i - 1] < _ngram)
                    if (!ProcessNGram(_ngram & ngramBitMaks[i])) return false;

            return true;
        }

        private bool ProcessNGram(ulong ngram)
        {
            _recentCount = _sketch.UpdateAndQuery(ngram);

            // if recentCount is less than minSupport  return early;
            if (_recentCount < _minSupport) return true;

            // if recentCount is greater than minSupport  and enqueued, we update early and return
            if (topNMap.ContainsKey(ngram))
            {
                topNMap[ngram] = _recentCount;
                return true;
            }

            // if recentCount is greater than minSupport  and not enqueued , but no capacity we return (superfluous?)
            if (topNMap.Count >= _capacity) return false;

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

            return true;
        }
    }

}
