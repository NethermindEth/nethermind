namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public interface IStatsAnalyzerTxTracer<TTrace>
{
    TTrace BuildResult(long fromBlock = 0, long toBlock = 0);

    /// <summary>
    /// When <c>true</c>, per-tx hot paths must short-circuit. Used to skip
    /// blocks executed under parallel BAL where the shared mutable analyzer
    /// state would race across worker threads.
    /// </summary>
    void SetSkip(bool skip);
}
