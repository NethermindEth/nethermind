using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer;

public interface IStatsAnalyzer<TData, TStat>
{
    public void Add(IEnumerable<TData> items);
    public void Add(TData item);
    public IEnumerable<TStat> Stats(SortOrder order);
}
