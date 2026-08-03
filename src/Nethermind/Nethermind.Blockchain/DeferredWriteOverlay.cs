// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

/// <summary>
/// Read-through overlay of block-data writes deferred to the shared <see cref="IDeferredBlockDataWriter"/>.
/// Entries are published synchronously so reads never miss them, and removed value-conditionally (on
/// reference identity) only after their write returns, so a delete or re-insert is never resurrected.
/// </summary>
/// <typeparam name="TPayload">What a reader needs and the write consumes (e.g. encoded bytes).</typeparam>
/// <param name="writer">Background writer the durable write is queued on.</param>
/// <param name="write">Writes a payload to the database (block number, block hash, payload).</param>
/// <param name="sharedLock">Lock serialising writes against removals. Pass the owner's lock when it has
/// other state to keep consistent with this overlay (the receipt store's tx-index); else a private lock.</param>
internal sealed class DeferredWriteOverlay<TPayload>(
    IDeferredBlockDataWriter writer,
    Action<ulong, Hash256, TPayload> write,
    Lock? sharedLock = null)
{
    private readonly ConcurrentDictionary<ValueHash256, Entry> _pending = new();
    private readonly Lock _lock = sharedLock ?? new Lock();
    private long _nextOperationId;
    private int _pendingCount;

    private sealed class Entry(
        DeferredWriteOverlay<TPayload> owner,
        long operationId,
        ulong blockNumber,
        Hash256 blockHash,
        TPayload payload) : IDeferredWriteOperation
    {
        public long OperationId { get; } = operationId;
        public ulong BlockNumber { get; } = blockNumber;
        public Hash256 BlockHash { get; } = blockHash;
        public TPayload Payload { get; } = payload;

        public void Execute() => owner.Persist(BlockHash.ValueHash256, OperationId);
    }

    public void Publish(ulong blockNumber, Hash256 blockHash, TPayload payload)
    {
        ValueHash256 key = blockHash.ValueHash256;
        while (true)
        {
            if (_pending.TryGetValue(key, out Entry? current))
            {
                Entry replacement = new(this, current.OperationId, blockNumber, blockHash, payload);
                if (_pending.TryUpdate(key, replacement, current)) return;
                continue;
            }

            Entry entry = new(this, Interlocked.Increment(ref _nextOperationId), blockNumber, blockHash, payload);

            // Increment first so a reader can observe a harmless false positive, never a false zero after publication.
            Interlocked.Increment(ref _pendingCount);
            if (_pending.TryAdd(key, entry))
            {
                writer.Enqueue(entry);
                return;
            }

            Interlocked.Decrement(ref _pendingCount);
        }
    }

    private void Persist(ValueHash256 key, long operationId)
    {
        lock (_lock)
        {
            while (_pending.TryGetValue(key, out Entry? current) && current.OperationId == operationId)
            {
                write(current.BlockNumber, current.BlockHash, current.Payload);
                if (_pending.TryRemove(new KeyValuePair<ValueHash256, Entry>(key, current)))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    return;
                }

                // A publisher replaced the payload while it was being written. Persist the latest value under
                // the same queued operation so the superseded publication needs no additional channel item.
            }
        }
    }

    public bool TryGet(Hash256 blockHash, out TPayload payload)
    {
        if (Volatile.Read(ref _pendingCount) != 0 && _pending.TryGetValue(blockHash.ValueHash256, out Entry? entry))
        {
            payload = entry.Payload;
            return true;
        }

        payload = default!;
        return false;
    }

    public bool Contains(Hash256 blockHash) =>
        Volatile.Read(ref _pendingCount) != 0 && _pending.ContainsKey(blockHash.ValueHash256);

    /// <summary>
    /// Removes the overlay entry and runs <paramref name="alsoUnderLock"/> atomically against a queued
    /// write, so a synchronous delete of the block's other data cannot interleave and be resurrected.
    /// </summary>
    public void Remove(Hash256 blockHash, Action alsoUnderLock)
    {
        lock (_lock)
        {
            if (_pending.TryRemove(blockHash.ValueHash256, out _))
            {
                Interlocked.Decrement(ref _pendingCount);
            }
            alsoUnderLock();
        }
    }
}
