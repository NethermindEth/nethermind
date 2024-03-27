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
        while (_batches.TryDequeue(out INodeStorage.WriteBatch batch))
        {
            batch.Dispose();
        }
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
    {
        INodeStorage.WriteBatch? currentBatch = RentBatch();
        currentBatch.Set(address, path, currentNodeKeccak, data, writeFlags);
        ReturnBatch(currentBatch);
    }

    public void Remove(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak)
    {
        INodeStorage.WriteBatch? currentBatch = RentBatch();
        currentBatch.Remove(address, path, currentNodeKeccak);
        ReturnBatch(currentBatch);
    }

    private INodeStorage.WriteBatch? RentBatch()
    {
        if (_disposing) throw new InvalidOperationException("Trying to set while disposing");
        if (!_batches.TryDequeue(out INodeStorage.WriteBatch? currentBatch))
        {
            currentBatch = _underlyingDb.StartWriteBatch();
        }

        return currentBatch;
    }

    private void ReturnBatch(INodeStorage.WriteBatch? currentBatch)
    {
        long val = Interlocked.Increment(ref _counter);
        if (val % 10000 == 0)
        {
            // Occasionally, we need to dispose the batch or it will take up memory usage.
            currentBatch.Dispose();
        }
        else
        {
            _batches.Enqueue(currentBatch);
        }
    }
}
