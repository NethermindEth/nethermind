using System;
using System.Collections.Generic;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{

    public interface IQueueProcessor<T>
    {
        void Add(IEnumerable<T> items);
    }

    public class ProcessingQueue<T> : IDisposable
    {

        private IQueueProcessor<T> _queueProcessor;
        private T[] _queue;
        private int bufferPos = 0;
        private bool disposed = false;

        public ProcessingQueue(int size, IQueueProcessor<T> queueProcessor)
        {
            _queueProcessor = queueProcessor;
            _queue = new T[size];
        }

        public void Enqueue(T item)
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
                    _queueProcessor.Add(_queue[..bufferPos]);
                }
                disposed = true;
            }
        }

        ~ProcessingQueue()
        {
            Dispose(disposing: false);
        }
    }

}

