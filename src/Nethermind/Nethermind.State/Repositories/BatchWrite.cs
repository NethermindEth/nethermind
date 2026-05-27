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
        private bool _lockTaken;
        private int _disposed;

        public BatchWrite(object lockObject, IWriteBatch writeBatch)
        {
            _lockObject = lockObject;
            Monitor.Enter(_lockObject, ref _lockTaken);

            WriteBatch = writeBatch;
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

        public IWriteBatch WriteBatch { get; }

        public bool Disposed => _disposed != 0;
    }
}
