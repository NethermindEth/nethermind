using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer;

public readonly record struct CallStat(Address Address, ulong Count);

public class CallStatsAnalyzer : StatsAnalyzer<Address>
{
    private readonly Dictionary<Address, ulong> _counts;
    private readonly object _lock = new();


    private readonly int _topN;
    private readonly Dictionary<Address, ulong> _topNMap;
    private readonly PriorityQueue<Address, ulong> _topNQueue;
    private ulong _max = 1;
    private ulong _minSupport = 1;


    public CallStatsAnalyzer(int topN)
    {
        _topN = topN;
        _topNQueue = new PriorityQueue<Address, ulong>(_topN);
        _topNMap = new Dictionary<Address, ulong>();
        _counts = new Dictionary<Address, ulong>();
    }


    public IEnumerable<CallStat> Stats
    {
        get
        {
            lock (_lock)
            {
                foreach (var (address, count) in _topNQueue.UnorderedItems) yield return new CallStat(address, count);
            }
        }
    }

    public IEnumerable<CallStat> StatsAscending
    {
        get
        {
            lock (_lock)
            {
                var queue = new PriorityQueue<Address, ulong>(_topN);
                while (queue.Count > 0)
                    if (queue.TryDequeue(out var address, out var count))
                        yield return new CallStat(address, count);
            }
        }
    }


    public override void Add(IEnumerable<Address> calls)
    {
        lock (_lock)
        {
            _topNQueue.Clear();
            foreach (var address in calls)
            {
                var callCount = 1 + CollectionsMarshal.GetValueRefOrAddDefault(_counts, address, out _);
                _counts[address] = callCount;
                if (callCount >= _minSupport) _topNMap[address] = callCount;
                else _topNMap.Remove(address);
            }

            ProcessTopN();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Add(Address call)
    {
        Add(new[] { call });
    }


    private void ProcessTopN()
    {
        _topNQueue.Clear();

        foreach (var kvp in _topNMap)
        {
            _max = Math.Max(_max, kvp.Value);

            if (_topNQueue.Count < _topN)
                _topNQueue.Enqueue(kvp.Key, kvp.Value);

            if (_topNQueue.Count < _topN) continue;
            _topNQueue.TryPeek(out _, out var min);
            if (min < kvp.Value) _topNQueue.DequeueEnqueue(kvp.Key, kvp.Value);
            //Queue has filled up, we update min support to filter out lower count updates
            _topNQueue.TryPeek(out _, out _minSupport);
        }
    }
}
