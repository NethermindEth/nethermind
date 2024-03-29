// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Threading;
public class ReadWriteLockDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();

    public ReadLock AcquireRead()
    {
        return new ReadLock(_lock);
    }

    public WriteLock AcquireWrite()
    {
        return new WriteLock(_lock);
    }

    public struct ReadLock : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        public ReadLock(ReaderWriterLockSlim @lock)
        {
            _lock = @lock;
            _lock.EnterReadLock();
        }

        public void Dispose()
        {
            _lock.ExitReadLock();
        }
    }

    public struct WriteLock : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        public WriteLock(ReaderWriterLockSlim @lock)
        {
            _lock = @lock;
            _lock.EnterReadLock();
        }

        public void Dispose()
        {
            _lock.ExitReadLock();
        }
    }
}
