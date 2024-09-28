using System;
using System.Collections.Generic;
using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{

    public class OpcodeStatsQueue : IDisposable
    {

        private StatsAnalyzer _statsAnalyzer;
        private DisposableResettableList<Instruction> _queue;
        private bool disposed = false;

        public OpcodeStatsQueue(DisposableResettableList<Instruction> buffer, StatsAnalyzer statsAnalyzer)
        {
            _statsAnalyzer = statsAnalyzer;
            _queue = buffer;
        }

        public void Enqueue(Instruction item)
        {
            _queue.Add(item);
         //   if (bufferPos < _queue.Length)
         //   {
         //       _queue[bufferPos] = item;
         //       bufferPos++;
         //   }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    _statsAnalyzer.Add(_queue);
                    _queue.Reset();

                }
                disposed = true;
            }
        }

        ~OpcodeStatsQueue()
        {
            Dispose(disposing: false);
        }
    }

}

