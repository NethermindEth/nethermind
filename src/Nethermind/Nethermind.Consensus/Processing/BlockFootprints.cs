// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>The footprints of one block's warm runs, by transaction index.</summary>
/// <remarks>
/// The storage writes and reads of the footprints are indexed by slot, so a footprint that read a slot an earlier
/// transaction writes, at a value other than the one that write leaves, is known to be invalidated and can be refreshed:
/// run again on the values the earlier footprints leave. Block processing reports the writes of the transactions it
/// executes, which take the place of what their footprints predicted.
/// </remarks>
internal sealed class BlockFootprints(Block block)
{
    /// <summary>Transactions touching one slot past which it is no longer indexed, and its readers no longer refreshed for it.</summary>
    internal const int MaxIndexedPerSlot = 64;

    /// <summary>Refreshes one block may take.</summary>
    internal const int MaxRefreshesPerBlock = 128;

    private readonly Hash256? _blockHash = block.Hash;
    private readonly TransactionFootprint?[] _footprints = new TransactionFootprint?[block.Transactions.Length];

    private readonly Lock _lock = new();
    // By slot, in ascending position: the values footprints write, and the values footprints read.
    private readonly Dictionary<StorageCell, List<(int Position, UInt256 Value)>> _writes = [];
    private readonly Dictionary<StorageCell, List<(int Position, UInt256 Value)>> _reads = [];
    private readonly Dictionary<int, (StorageCell[] Written, StorageCell[] Read)> _indexed = [];
    private readonly HashSet<StorageCell> _unindexed = [];
    private readonly SortedSet<int> _invalidated = [];
    private int _refreshesTaken;

    // Written by block processing, applied by the refresh worker, so block processing never takes the lock.
    private readonly ConcurrentQueue<(int Position, List<(StorageCell Cell, UInt256 Value)> Writes)> _executed = new();
    private readonly SemaphoreSlim _changed = new(0);
    private volatile bool _warmPassEnded;

    /// <remarks>
    /// Needs receipts without a per-transaction state root (EIP-658). EIP-8037 gas accounting and a block access list
    /// (EIP-7928) are built while transactions execute, which a replay does not.
    /// </remarks>
    public static bool AppliesTo(Block block, IReleaseSpec spec) =>
        block.Transactions.Length > 0
        && spec.IsEip658Enabled
        && !spec.IsEip8037Enabled
        && !spec.BlockLevelAccessListsEnabled
        && block.BlockAccessList is null;

    /// <remarks>
    /// A warm run skips the pre-execution checks; the nonce is checked when it is recorded and the rest when it is
    /// replayed, except a nonce that would overflow (EIP-2681) and a zero fee, which are excluded here.
    /// </remarks>
    public static bool IsRecordable(Transaction tx) =>
        tx.SenderAddress is not null
        && !tx.SupportsFrames
        && !tx.IsSystem()
        && tx.Nonce != ulong.MaxValue
        && !(tx.MaxFeePerGas.IsZero && tx.MaxPriorityFeePerGas.IsZero);

    public int Count => _footprints.Length;

    public TransactionFootprint? Get(int index) => Volatile.Read(ref _footprints[index]);

    public void Store(int index, TransactionFootprint footprint)
    {
        Volatile.Write(ref _footprints[index], footprint);
        Index(index, footprint);
    }

    /// <summary>The footprint of <paramref name="tx"/>, at <paramref name="index"/> in the block <paramref name="header"/> heads.</summary>
    public TransactionFootprint? Find(int index, Transaction tx, BlockHeader header)
    {
        TransactionFootprint?[] footprints = _footprints;
        if ((uint)index >= (uint)footprints.Length || _blockHash is null || header.Hash != _blockHash) return null;
        TransactionFootprint? footprint = Volatile.Read(ref footprints[index]);
        return footprint is not null && ReferenceEquals(footprint.Transaction, tx) ? footprint : null;
    }

    /// <summary>The value the footprints before <paramref name="position"/> leave <paramref name="cell"/> at; null when none writes it.</summary>
    public UInt256? ValueBefore(in StorageCell cell, int position)
    {
        lock (_lock) return ValueBeforeLocked(in cell, position);
    }

    /// <summary>Whether the footprint read a slot too many transactions touch to be indexed.</summary>
    public bool ReadsUnindexedSlot(TransactionFootprint footprint)
    {
        lock (_lock)
        {
            if (_unindexed.Count == 0) return false;
            foreach (ref readonly SlotPrecondition slot in footprint.Slots)
            {
                if (_unindexed.Contains(slot.Cell)) return true;
            }

            return false;
        }
    }

    /// <summary>Takes the first invalidated position after <paramref name="after"/>, dropping the ones before it.</summary>
    public bool TryTakeInvalidated(int after, out int position)
    {
        lock (_lock)
        {
            while (_invalidated.Count > 0 && _refreshesTaken < MaxRefreshesPerBlock)
            {
                int first = _invalidated.Min;
                _invalidated.Remove(first);
                if (first <= after) continue;
                _refreshesTaken++;
                position = first;
                return true;
            }
        }

        position = -1;
        return false;
    }

    /// <summary>Whether a refresh or a write block processing reported is waiting.</summary>
    public bool HasWork
    {
        get
        {
            if (!_executed.IsEmpty) return true;
            lock (_lock) return _invalidated.Count > 0 && _refreshesTaken < MaxRefreshesPerBlock;
        }
    }

    public bool WarmPassEnded => _warmPassEnded;

    public void EndWarmPass()
    {
        _warmPassEnded = true;
        _changed.Release();
    }

    /// <summary>Waits until a footprint is invalidated, block processing reports writes or the warm pass ends.</summary>
    public void WaitForWork(TimeSpan timeout, CancellationToken token)
    {
        try
        {
            _changed.Wait(timeout, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Queues the storage writes block processing committed when it executed the transaction at <paramref name="position"/>.</summary>
    public void QueueExecuted(int position, List<(StorageCell Cell, UInt256 Value)> writes)
    {
        _executed.Enqueue((position, writes));
        _changed.Release();
    }

    /// <summary>Puts the writes block processing reported in place of what their footprints predicted, in the order it executed them.</summary>
    public void ApplyExecuted()
    {
        while (_executed.TryDequeue(out (int Position, List<(StorageCell Cell, UInt256 Value)> Writes) executed))
        {
            ApplyExecuted(executed.Position, executed.Writes);
        }
    }

    private void ApplyExecuted(int position, List<(StorageCell Cell, UInt256 Value)> writes)
    {
        if ((uint)position >= (uint)_footprints.Length) return;
        bool invalidated = false;
        lock (_lock)
        {
            // The transaction is done: its reads no longer invalidate it, and its writes are the ones it made.
            StorageCell[]? predicted = Unindex(position);
            List<StorageCell> written = new(writes.Count);
            foreach ((StorageCell cell, UInt256 value) in writes)
            {
                if (IndexOf(_writes, in cell) is not { } versions) continue;
                Insert(versions, position, in value);
                written.Add(cell);
            }

            foreach (StorageCell cell in written) invalidated |= InvalidateReaders(in cell, position);
            if (predicted is not null)
            {
                foreach (StorageCell cell in predicted) invalidated |= InvalidateReaders(in cell, position);
            }

            _indexed[position] = ([.. written], []);
        }

        if (invalidated) _changed.Release();
    }

    private void Index(int position, TransactionFootprint footprint)
    {
        Dictionary<StorageCell, UInt256>? writes = null;
        foreach (ref readonly StateEffect effect in footprint.Effects)
        {
            if (effect.Kind == EffectKind.SetStorage) (writes ??= [])[new StorageCell(effect.Address, in effect.Index)] = effect.Value;
        }

        ReadOnlySpan<SlotPrecondition> reads = footprint.Slots;
        bool invalidated = false;
        lock (_lock)
        {
            // A refreshed footprint replaces what the previous one wrote and read.
            StorageCell[]? previous = Unindex(position);
            List<StorageCell> written = new(writes?.Count ?? 0);
            if (writes is not null)
            {
                foreach ((StorageCell cell, UInt256 value) in writes)
                {
                    if (IndexOf(_writes, in cell) is not { } versions) continue;
                    Insert(versions, position, in value);
                    written.Add(cell);
                }
            }

            foreach (StorageCell cell in written) invalidated |= InvalidateReaders(in cell, position);
            if (previous is not null)
            {
                foreach (StorageCell cell in previous) invalidated |= InvalidateReaders(in cell, position);
            }

            List<StorageCell> read = new(reads.Length);
            bool outdated = false;
            foreach (ref readonly SlotPrecondition slot in reads)
            {
                if (IndexOf(_reads, in slot.Cell) is not { } readers) continue;
                readers.Add((position, slot.Value));
                read.Add(slot.Cell);
                outdated |= ValueBeforeLocked(in slot.Cell, position) is { } value && value != slot.Value;
            }

            _indexed[position] = ([.. written], [.. read]);
            if (outdated) invalidated |= _invalidated.Add(position);
        }

        if (invalidated) _changed.Release();
    }

    // Drops what the position's previous footprint, or the transaction executed there, wrote and read; returns what it wrote.
    private StorageCell[]? Unindex(int position)
    {
        if (!_indexed.Remove(position, out (StorageCell[] Written, StorageCell[] Read) previous)) return null;
        foreach (StorageCell cell in previous.Written)
        {
            if (_writes.TryGetValue(cell, out List<(int Position, UInt256 Value)>? versions)) RemovePosition(versions, position);
        }

        foreach (StorageCell cell in previous.Read)
        {
            if (_reads.TryGetValue(cell, out List<(int Position, UInt256 Value)>? readers)) RemovePosition(readers, position);
        }

        return previous.Written;
    }

    // The slot's list in the index, created on first use; null for a slot too many transactions touch, which then leaves
    // both indexes.
    private List<(int Position, UInt256 Value)>? IndexOf(Dictionary<StorageCell, List<(int Position, UInt256 Value)>> index, in StorageCell cell)
    {
        if (_unindexed.Contains(cell)) return null;
        if (index.TryGetValue(cell, out List<(int Position, UInt256 Value)>? list))
        {
            if (list.Count < MaxIndexedPerSlot) return list;
            _unindexed.Add(cell);
            _writes.Remove(cell);
            _reads.Remove(cell);
            return null;
        }

        list = [];
        index[cell] = list;
        return list;
    }

    // Marks the later footprints that read the slot at a value other than the one the block now leaves before them.
    private bool InvalidateReaders(in StorageCell cell, int position)
    {
        if (!_reads.TryGetValue(cell, out List<(int Position, UInt256 Value)>? readers)) return false;
        bool invalidated = false;
        foreach ((int reader, UInt256 read) in readers)
        {
            if (reader > position && ValueBeforeLocked(in cell, reader) is { } value && value != read) invalidated |= _invalidated.Add(reader);
        }

        return invalidated;
    }

    private UInt256? ValueBeforeLocked(in StorageCell cell, int position)
    {
        if (!_writes.TryGetValue(cell, out List<(int Position, UInt256 Value)>? versions)) return null;
        int at = FirstAtOrAfter(versions, position);
        return at == 0 ? null : versions[at - 1].Value;
    }

    private static int FirstAtOrAfter(List<(int Position, UInt256 Value)> versions, int position)
    {
        int low = 0, high = versions.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (versions[mid].Position < position) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    private static void Insert(List<(int Position, UInt256 Value)> versions, int position, in UInt256 value) =>
        versions.Insert(FirstAtOrAfter(versions, position), (position, value));

    private static void RemovePosition(List<(int Position, UInt256 Value)> entries, int position)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Position == position) entries.RemoveAt(i);
        }
    }
}
