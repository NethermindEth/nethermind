using Nethermind.Core.Resettables;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer;

public sealed class StatsProcessingQueue(
    DisposableResettableList<Instruction> buffer,
    StatsAnalyzer statsAnalyzer)
    : IDisposable
{
    private bool disposed;

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
        if (disposed) return;
        if (disposing)
        {
            statsAnalyzer.Add(buffer);
            buffer.Reset();
        }

        disposed = true;
    }

    ~StatsProcessingQueue()
    {
        Dispose(false);
    }
}
