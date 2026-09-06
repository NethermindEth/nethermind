using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.StatsAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer.Call;

public readonly record struct CallStat(Address Address, ulong Count);

public class CallStatsAnalyzer(int topN) : TopNAnalyzer<Address, Address, CallStat>(topN)
{
    private readonly Dictionary<Address, ulong> _counts = [];


    public override void Add(IEnumerable<Address> calls)
    {
        TopNQueue.Clear();
        foreach (Address address in calls)
        {
            ulong callCount = 1 + CollectionsMarshal.GetValueRefOrAddDefault(_counts, address, out _);
            _counts[address] = callCount;
            if (callCount >= MinSupport) TopNMap[address] = callCount;
            else TopNMap.Remove(address);
        }

        ProcessTopN();
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Add(Address call) => Add([call]);


    public override IEnumerable<CallStat> Stats(SortOrder order)
    {

        switch (order)
        {
            case SortOrder.Unordered:
                foreach ((Address address, ulong count) in TopNQueue.UnorderedItems)
                    yield return new CallStat(address, count);
                break;
            case SortOrder.Ascending:
                PriorityQueue<Address, ulong> queue = new(TopN);
                foreach ((Address address, ulong count) in TopNQueue.UnorderedItems) queue.Enqueue(address, count);
                while (queue.TryDequeue(out Address? addressAsc, out ulong countAsc))
                    yield return new CallStat(addressAsc, countAsc);
                break;
            case SortOrder.Descending:
                PriorityQueue<Address, ulong> queueDecending =
                    new(TopN, Comparer<ulong>.Create((x, y) => y.CompareTo(x)));
                foreach ((Address address, ulong count) in TopNQueue.UnorderedItems) queueDecending.Enqueue(address, count);
                while (queueDecending.Count > 0)
                    if (queueDecending.TryDequeue(out Address? address, out ulong count))
                        yield return new CallStat(address, count);
                break;
        }
    }
}
