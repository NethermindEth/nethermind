using Nethermind.Core.Resettables;

namespace Nethermind.StatsAnalyzer.Plugin.Analyzer;

public sealed class StatsProcessingQueue<T,U>(
    DisposableResettableList<T> buffer,
    IStatsAnalyzer<T,U> statsAnalyzer,
    CancellationToken ct)
    : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void Enqueue(T item)
    {
        buffer.Add(item);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing && !ct.IsCancellationRequested)
        {
            statsAnalyzer.Add(buffer);
            buffer.Reset();
        }

        _disposed = true;
    }

    ~StatsProcessingQueue()
    {
        Dispose(false);
    }
}
