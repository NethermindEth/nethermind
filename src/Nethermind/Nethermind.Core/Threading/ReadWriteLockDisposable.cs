// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;
public class ReadWriteLockDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();

    public ReadLock AcquireRead() => new(_lock);

    public WriteLock AcquireWrite() => new(_lock);

    public readonly struct ReadLock : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        public ReadLock(ReaderWriterLockSlim @lock)
        {
            _lock = @lock;
            _lock.EnterReadLock();
        }

        public void Dispose() => _lock.ExitReadLock();
    }

    public readonly struct WriteLock : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        public WriteLock(ReaderWriterLockSlim @lock)
        {
            _lock = @lock;
            _lock.EnterWriteLock();
        }

        public void Dispose() => _lock.ExitWriteLock();
    }
}
