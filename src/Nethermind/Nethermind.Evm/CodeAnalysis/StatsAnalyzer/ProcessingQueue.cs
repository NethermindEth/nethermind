using System;
using System.Collections.Generic;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{

    public enum Level
    {
        Block,
        Transaction,
        Global,
    }

    public interface IStatsAccumulator<T>
    {
        void Add(Level level, ulong id, IEnumerable<T> items);
    }

    public class ProcessingQueue<T> : IDisposable
    {

        public readonly ulong id;
        private Level _level;
        private IStatsAccumulator<T> _statsAccumulator;
        private T[] _queue;
        private int bufferPos = 0;
        private bool disposed = false;

        public ProcessingQueue(Level level, ulong id, int size, IStatsAccumulator<T> statsAccumulator)
        {
            _level = level;
            this.id = id;
            _statsAccumulator = statsAccumulator;
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
                    _statsAccumulator.Add(_level, id, _queue[..bufferPos]);
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

