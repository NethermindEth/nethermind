// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// Rust style wrapper of locked item. Make it a bit easier to know which object this lock is protecting.
/// </summary>
/// <typeparam name="T"></typeparam>
public class ReadWriteLockBox<T>
{
    private readonly ReaderWriterLockSlim _lock;
    private readonly T _item;

    public ReadWriteLockBox(T item)
    {
        _item = item;
        _lock = new ReaderWriterLockSlim();
    }

    public LockExitor EnterReadLock(out T item)
    {
        item = _item;
        _lock.EnterReadLock();
        return new LockExitor(_lock, true);
    }

    public LockExitor EnterWriteLock(out T item)
    {
        item = _item;
        _lock.EnterWriteLock();
        return new LockExitor(_lock, false);
    }

    public ref struct LockExitor(ReaderWriterLockSlim @lock, bool read) : IDisposable
    {
        public void Dispose()
        {
            if (read)
            {
                @lock.ExitReadLock();
            }
            else
            {
                @lock.ExitWriteLock();
            }
        }
    }
}
