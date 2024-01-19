// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Nethermind.Core.Utils;

public class WriteBatcher: IWriteBatch
{
    private long _counter = 0;
    private readonly ConcurrentQueue<IWriteBatch> _batches = new();
    private readonly IKeyValueStoreWithBatching _underlyingDb;
    private bool _disposing = false;

    public WriteBatcher(IKeyValueStoreWithBatching underlyingDb)
    {
        _underlyingDb = underlyingDb;
    }

    public void Dispose()
    {
        _disposing = true;
        foreach (IWriteBatch batch in _batches)
        {
            batch.Dispose();
        }
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        if (_disposing) throw new InvalidOperationException("Trying to set while disposing");
        if (!_batches.TryDequeue(out IWriteBatch? currentBatch))
        {
            currentBatch = _underlyingDb.StartWriteBatch();
        }

        currentBatch.Set(key, value, flags);
        long val = Interlocked.Increment(ref _counter);
        if (val % 10000 == 0)
        {
            currentBatch.Dispose();
        }
        else
        {
            _batches.Enqueue(currentBatch);
        }
    }
}
