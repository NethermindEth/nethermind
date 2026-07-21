using Nethermind.Core.Resettables;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer;

public sealed class StatsProcessingQueue<TData, TStat>(
    ResettableList<TData> buffer,
    IStatsAnalyzer<TData, TStat> statsAnalyzer,
    CancellationToken ct)
    : IDisposable
{
    private bool _disposed;

    public void Enqueue(TData item) => buffer.Add(item);

    public void Dispose()
    {
        if (_disposed) return;
        if (!ct.IsCancellationRequested)
        {
            statsAnalyzer.Add(buffer);
            buffer.Reset();
        }

        _disposed = true;
    }
}
