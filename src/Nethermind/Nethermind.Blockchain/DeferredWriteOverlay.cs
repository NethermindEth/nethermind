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

    private sealed class Entry(ulong blockNumber, Hash256 blockHash, TPayload payload)
    {
        public ulong BlockNumber { get; } = blockNumber;
        public Hash256 BlockHash { get; } = blockHash;
        public TPayload Payload { get; } = payload;
    }

    public void Publish(ulong blockNumber, Hash256 blockHash, TPayload payload)
    {
        Entry entry = new(blockNumber, blockHash, payload);
        _pending[blockHash.ValueHash256] = entry;
        writer.Enqueue(() => Persist(entry));
    }

    private void Persist(Entry entry)
    {
        lock (_lock)
        {
            // Skip if a removal dropped this exact entry; value-conditional so a queued re-insert keeps its own.
            ValueHash256 key = entry.BlockHash.ValueHash256;
            if (!_pending.TryGetValue(key, out Entry? current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            write(entry.BlockNumber, entry.BlockHash, entry.Payload);
            _pending.TryRemove(new KeyValuePair<ValueHash256, Entry>(key, entry));
        }
    }

    public bool TryGet(Hash256 blockHash, out TPayload payload)
    {
        if (_pending.TryGetValue(blockHash.ValueHash256, out Entry? entry))
        {
            payload = entry.Payload;
            return true;
        }

        payload = default!;
        return false;
    }

    public bool Contains(Hash256 blockHash) => _pending.ContainsKey(blockHash.ValueHash256);

    /// <summary>
    /// Removes the overlay entry and runs <paramref name="alsoUnderLock"/> atomically against a queued
    /// write, so a synchronous delete of the block's other data cannot interleave and be resurrected.
    /// </summary>
    public void Remove(Hash256 blockHash, Action alsoUnderLock)
    {
        lock (_lock)
        {
            _pending.TryRemove(blockHash.ValueHash256, out _);
            alsoUnderLock();
        }
    }
}
