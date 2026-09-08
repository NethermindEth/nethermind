// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.State.Repositories
{
    public class BatchWrite : IDisposable
    {
        private readonly object _lockObject;
        private readonly Func<IWriteBatch> _writeBatchFactory;
        private bool _lockTaken;
        private int _disposed;

        public BatchWrite(object lockObject, Func<IWriteBatch> writeBatchFactory)
        {
            _lockObject = lockObject;
            _writeBatchFactory = writeBatchFactory;
            Monitor.Enter(_lockObject, ref _lockTaken);

            try
            {
                WriteBatch = _writeBatchFactory();
            }
            catch
            {
                if (_lockTaken)
                {
                    _lockTaken = false;
                    Monitor.Exit(_lockObject);
                }
                throw;
            }
        }

        /// <summary>Writes the accumulated batch and starts a fresh one, keeping the write lock held.</summary>
        /// <remarks>Splits atomicity at each flush — each segment is atomic on its own.</remarks>
        public void Flush()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            IWriteBatch old = WriteBatch;
            WriteBatch = _writeBatchFactory();
            old.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                WriteBatch.Dispose();
            }
            finally
            {
                if (_lockTaken)
                {
                    _lockTaken = false;
                    Monitor.Exit(_lockObject);
                }
            }
        }

        public IWriteBatch WriteBatch { get; private set; }

        public bool Disposed => _disposed != 0;
    }
}
