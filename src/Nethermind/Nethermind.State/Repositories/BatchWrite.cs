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

        public BatchWrite(object lockObject, IWriteBatch writeBatch)
        {
            _lockObject = lockObject;
            Monitor.Enter(_lockObject, ref _lockTaken);

            WriteBatch = writeBatch;
        }

        public void Dispose()
        {
            if (!Disposed)
            {
                WriteBatch.Dispose();

                if (_lockTaken)
                {
                    _lockTaken = false;
                    Monitor.Exit(_lockObject);
                }

                Disposed = true;
            }
        }

        public IWriteBatch WriteBatch { get; }

        public bool Disposed { get; private set; }
    }
}
