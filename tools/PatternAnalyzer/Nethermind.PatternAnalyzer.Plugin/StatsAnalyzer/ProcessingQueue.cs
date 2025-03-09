using Nethermind.Core.Resettables;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer
{

    public sealed class StatsProcessingQueue(DisposableResettableList<Instruction> buffer, Analyzer.StatsAnalyzer statsAnalyzer)
        : IDisposable
    {
        private bool disposed = false;

        public void Enqueue(Instruction item)
        {
            buffer.Add(item);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (this.disposed) return;
            if (disposing)
            {
                statsAnalyzer.Add(buffer);
                buffer.Reset();

            }
            disposed = true;
        }

        ~StatsProcessingQueue()
        {
            Dispose(disposing: false);
        }
    }

}

