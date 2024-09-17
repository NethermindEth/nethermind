using System;
using System.Collections.Generic;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{

    public class OpcodeStatsQueue : IDisposable
    {

        private StatsAnalyzer _statsAnalyzer;
        private Instruction[] _queue;
        private int bufferPos = 0;
        private bool disposed = false;
        private readonly McsLock _processingLock ;

        public OpcodeStatsQueue(int size, StatsAnalyzer statsAnalyzer, McsLock processingLock, Action<StatsAnalyzer> postProcessing = default)
        {
            _statsAnalyzer = statsAnalyzer;
            _queue = new Instruction[size];
            _processingLock = processingLock;
        }

        public void Enqueue(Instruction item)
        {
            if (bufferPos < _queue.Length)
            {
                _queue[bufferPos] = item;
                bufferPos++;
            }
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
                    _processingLock.Acquire();
                    _statsAnalyzer.Add(_queue[..bufferPos]);
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

