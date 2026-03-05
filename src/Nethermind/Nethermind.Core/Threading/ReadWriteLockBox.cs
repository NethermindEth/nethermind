// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// Rust style wrapper of locked item. Make it a bit easier to know which object this lock is protecting.
/// </summary>
/// <typeparam name="T"></typeparam>
public readonly struct ReadWriteLockBox<T>(T item)
{
    private readonly ReaderWriterLockSlim _lock = new();

    public Lock EnterReadLock(out T item1)
    {
        item1 = item;
        return new Lock(_lock, true);
    }

    public Lock EnterWriteLock(out T item1)
    {
        item1 = item;
        return new Lock(_lock, false);
    }

    public readonly ref struct Lock : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock;
        private readonly bool _read;

        public Lock(ReaderWriterLockSlim rwLock, bool read)
        {
            _rwLock = rwLock;
            _read = read;
            if (_read)
            {
                _rwLock.EnterReadLock();
            }
            else
            {
                _rwLock.EnterWriteLock();
            }
        }

        public void Dispose()
        {
            if (_read)
            {
                _rwLock.ExitReadLock();
            }
            else
            {
                _rwLock.ExitWriteLock();
            }
        }
    }
}
