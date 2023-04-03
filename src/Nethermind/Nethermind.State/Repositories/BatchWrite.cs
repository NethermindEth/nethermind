// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.State.Repositories
{
    public class BatchWrite : IDisposable
    {
        private readonly object _lockObject;
        private bool _lockTaken;

        public BatchWrite(object lockObject)
        {
            _lockObject = lockObject;
            Monitor.Enter(_lockObject, ref _lockTaken);
        }

        public void Dispose()
        {
            if (!Disposed)
            {
                if (_lockTaken)
                {
                    _lockTaken = false;
                    Monitor.Exit(_lockObject);
                }

                Disposed = true;
            }
        }

        public bool Disposed { get; private set; }
    }
}
