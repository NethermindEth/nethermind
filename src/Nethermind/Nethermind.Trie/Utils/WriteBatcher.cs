// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Core.Utils;

/// <summary>
/// Batches writes into a set of concurrent batches. For cases where throughput matter, but not atomicity.
/// </summary>
public class ConcurrentNodeWriteBatcher : INodeStorage.WriteBatch
{
    private long _counter = 0;
    private readonly ConcurrentQueue<INodeStorage.WriteBatch> _batches = new();
    private readonly INodeStorage _underlyingDb;
    private bool _disposing = false;

    public ConcurrentNodeWriteBatcher(INodeStorage underlyingDb)
    {
        _underlyingDb = underlyingDb;
    }

    public void Dispose()
    {
        _disposing = true;
        foreach (INodeStorage.WriteBatch batch in _batches)
        {
            batch.Dispose();
        }
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, byte[] toArray, WriteFlags writeFlags)
    {
        if (_disposing) throw new InvalidOperationException("Trying to set while disposing");
        if (!_batches.TryDequeue(out INodeStorage.WriteBatch? currentBatch))
        {
            currentBatch = _underlyingDb.StartWriteBatch();
        }

        currentBatch.Set(address, path, currentNodeKeccak, toArray, writeFlags);
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

    public void Remove(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak)
    {
        if (_disposing) throw new InvalidOperationException("Trying to set while disposing");
        if (!_batches.TryDequeue(out INodeStorage.WriteBatch? currentBatch))
        {
            currentBatch = _underlyingDb.StartWriteBatch();
        }

        currentBatch.Remove(address, path, currentNodeKeccak);
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
