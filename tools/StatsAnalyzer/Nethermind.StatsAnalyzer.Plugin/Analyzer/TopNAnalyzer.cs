using Nethermind.StatsAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer;

public abstract class TopNAnalyzer<TData, TEncoding, TStat>(int topN, int capacity = 1000) : IStatsAnalyzer<TData, TStat>
    where TEncoding : notnull
{
    protected readonly int TopN = topN;
    protected readonly Dictionary<TEncoding, ulong> TopNMap = new(capacity);
    protected readonly PriorityQueue<TEncoding, ulong> TopNQueue = new(topN);
    protected ulong Max = 1;
    protected ulong MinSupport = 1;

    public abstract void Add(IEnumerable<TData> items);

    public abstract void Add(TData item);

    public abstract IEnumerable<TStat> Stats(SortOrder order);

    public void ProcessTopN()
    {
        TopNQueue.Clear();

        foreach (KeyValuePair<TEncoding, ulong> kvp in TopNMap)
        {
            Max = Math.Max(Max, kvp.Value);

            if (TopNQueue.Count < TopN)
            {
                TopNQueue.Enqueue(kvp.Key, kvp.Value);
                continue;
            }

            TopNQueue.TryPeek(out _, out ulong min);
            if (min < kvp.Value) TopNQueue.DequeueEnqueue(kvp.Key, kvp.Value);
            TopNQueue.TryPeek(out _, out MinSupport);
        }
    }
}
