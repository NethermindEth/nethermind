namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public interface IStatsAnalyzerTxTracer<TTrace>
{
    TTrace BuildResult(ulong fromBlock = 0UL, ulong toBlock = 0UL);

    /// <summary>
    /// When <c>true</c>, per-tx hot paths must short-circuit. Used to skip
    /// blocks executed under parallel BAL where the shared mutable analyzer
    /// state would race across worker threads.
    /// </summary>
    void SetSkip(bool skip);
}
