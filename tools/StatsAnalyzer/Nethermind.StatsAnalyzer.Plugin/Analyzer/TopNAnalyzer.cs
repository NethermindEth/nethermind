
using System.Runtime.CompilerServices;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer;

public abstract class TopNAnalyzer<TData, TEncoding, TStat> : IStatsAnalyzer<TData, TStat>
        where TEncoding : notnull
    {

        protected readonly object Lock = new();
        protected readonly int TopN;
        protected readonly Dictionary<TEncoding, ulong> TopNMap;
        protected readonly PriorityQueue<TEncoding, ulong> TopNQueue;
        protected ulong MinSupport = 1;
        protected ulong Max = 1;

        protected TopNAnalyzer(int topN, int capacity = 1000)
        {
            TopN = topN;
            TopNQueue = new PriorityQueue<TEncoding, ulong>(topN);
            TopNMap =  new Dictionary<TEncoding, ulong>(capacity);
        }

        public abstract void Add(IEnumerable<TData> items);

        public abstract void Add(TData item);

        public void ProcessTopN()
        {
            TopNQueue.Clear();

            foreach (var kvp in TopNMap)
            {
                Max = Math.Max(Max, kvp.Value);

                if (TopNQueue.Count < TopN)
                    TopNQueue.Enqueue(kvp.Key, kvp.Value);

                if (TopNQueue.Count < TopN) continue;
                TopNQueue.TryPeek(out _, out var min);
                if (min < kvp.Value) TopNQueue.DequeueEnqueue(kvp.Key, kvp.Value);
                //Queue has filled up, we update min support to filter out lower count updates
                TopNQueue.TryPeek(out _, out MinSupport);
            }
        }

    public abstract IEnumerable<TStat> Stats(SortOrder order);
}
