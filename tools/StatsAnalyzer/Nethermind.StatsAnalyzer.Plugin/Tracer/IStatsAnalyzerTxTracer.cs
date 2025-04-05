namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public interface IStatsAnalyzerTxTracer<TTrace>
{
    TTrace BuildResult(long fromBlock = 0, long toBlock = 0);
};

