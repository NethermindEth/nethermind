using Nethermind.Core.Resettables;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer;

public sealed class StatsProcessingQueue(
    DisposableResettableList<Instruction> buffer,
    StatsAnalyzer statsAnalyzer,
    CancellationToken ct)
    : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void Enqueue(Instruction item)
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
