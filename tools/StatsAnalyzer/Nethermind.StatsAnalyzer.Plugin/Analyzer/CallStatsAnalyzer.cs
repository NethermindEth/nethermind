using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer;

public readonly record struct CallStat(Address Address, ulong Count);

public class CallStatsAnalyzer : TopNAnalyzer<Address,Address,CallStat>
{

    private readonly Dictionary<Address, ulong> _counts;



    public CallStatsAnalyzer(int topN) : base(topN)
    {
        _counts = new Dictionary<Address, ulong>();
    }


    public override unsafe void Add(IEnumerable<Address> calls)
    {
        lock (Lock)
        {

            TopNQueue.Clear();
            foreach (var address in calls)
            {
                var callCount =  1 + CollectionsMarshal.GetValueRefOrAddDefault(_counts, address, out _);
                _counts[address] = callCount;
                if (callCount >= MinSupport) TopNMap[address] = callCount;
                else TopNMap.Remove(address);
            }

            ProcessTopN();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override unsafe void Add(Address call)
    {
        Add(new []{call});
    }


    public override IEnumerable<CallStat> Stats(SortOrder order)
    {

        lock (Lock)
        {
            switch(order)
            {
                case SortOrder.Unordered :
                        foreach (var (address, count) in TopNQueue.UnorderedItems)
                        yield return new CallStat(address, count);
                        break;
                case SortOrder.Ascending :
                        var queue = new PriorityQueue<Address, ulong>(TopN);
                        while (queue.Count > 0)
                        {
                            if (queue.TryDequeue(out var address, out var count))
                                 yield return new CallStat(address, count);
                        }
                        break;
                case SortOrder.Descending :
                    var queueDecending = new PriorityQueue<Address, ulong>(TopN, Comparer<ulong>.Create((x, y) => y.CompareTo(x)));
                    foreach (var (address, count) in TopNQueue.UnorderedItems)
                    {
                        queueDecending.Enqueue(address, count);
                    }
                    while (queueDecending.Count > 0)
                    {
                        if (queueDecending.TryDequeue(out var address, out var count))
                            yield return new CallStat(address, count);
                    }
                    break;

            }
        }
    }


}
