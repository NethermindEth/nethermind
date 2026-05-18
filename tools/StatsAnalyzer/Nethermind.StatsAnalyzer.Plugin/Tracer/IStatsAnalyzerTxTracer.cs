namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public interface IStatsAnalyzerTxTracer<TTrace>
{
    TTrace BuildResult(long fromBlock = 0, long toBlock = 0);

    // When set, the tracer must short-circuit per-tx hot paths (StartOperation,
    // ReportAction). Used to skip blocks executed under parallel BAL mode where
    // the shared mutable analyzer state would race across worker threads.
    void SetSkip(bool skip);
}
