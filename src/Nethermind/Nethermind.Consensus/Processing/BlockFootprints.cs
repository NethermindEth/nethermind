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
    /// <summary>
    /// Footprints indexed for one slot's writes, and for its reads. Past it, a slot's writes are no longer tracked and the
    /// footprints that read it are not refreshed; a reader past it is only not invalidated through that slot.
    /// </summary>
    internal const int MaxIndexedPerSlot = 64;

    /// <summary>Refreshes one block may run.</summary>
    internal const int MaxRefreshesPerBlock = 128;

    private readonly Hash256? _blockHash = block.Hash;
    private readonly TransactionFootprint?[] _footprints = new TransactionFootprint?[block.Transactions.Length];

    private readonly Lock _lock = new();
    // By slot: the values footprints write, in ascending position, and the values footprints read.
    private readonly Dictionary<StorageCell, List<(int Position, UInt256 Value)>> _writes = [];
    private readonly Dictionary<StorageCell, List<(int Position, UInt256 Value)>> _reads = [];
    private readonly Dictionary<int, (StorageCell[] Written, StorageCell[] Read)> _indexed = [];
    private readonly HashSet<StorageCell> _untracked = [];
    private readonly bool[] _executed = new bool[block.Transactions.Length];
    private readonly SortedSet<int> _invalidated = [];
    private int _refreshesRun;

    // Written by block processing, applied by the refresh worker, so block processing never takes the lock.
    private readonly ConcurrentQueue<(int Position, List<(StorageCell Cell, UInt256 Value)>? Writes)> _reported = new();
    private readonly SemaphoreSlim _changed = new(0);
    private volatile bool _warmPassEnded;
    private volatile bool _waitsForBlockProcessing = true;

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

    /// <summary>
    /// The value the footprints before <paramref name="position"/> leave <paramref name="cell"/> at; null when none
    /// writes it, so it is at its parent value.
    /// </summary>
    public UInt256? ValueBefore(in StorageCell cell, int position)
    {
        lock (_lock) return ValueBeforeLocked(in cell, position);
    }

    /// <summary>Whether the footprint read a slot too many transactions write for its writes to be tracked.</summary>
    public bool ReadsUntrackedSlot(TransactionFootprint footprint)
    {
        lock (_lock)
        {
            if (_untracked.Count == 0) return false;
            foreach (ref readonly SlotPrecondition slot in footprint.Slots)
            {
                if (_untracked.Contains(slot.Cell)) return true;
            }

            return false;
        }
    }

    /// <summary>Takes the first invalidated position after <paramref name="after"/>, dropping the ones before it.</summary>
    public bool TryTakeInvalidated(int after, out int position)
    {
        lock (_lock)
        {
            while (_invalidated.Count > 0 && _refreshesRun < MaxRefreshesPerBlock)
            {
                int first = _invalidated.Min;
                _invalidated.Remove(first);
                if (first <= after) continue;
                position = first;
                return true;
            }
        }

        position = -1;
        return false;
    }

    /// <summary>Counts a refresh that runs; once the block has run its refreshes, no more are taken.</summary>
    public void CountRefresh()
    {
        lock (_lock) _refreshesRun++;
    }

    /// <summary>Whether a refresh or a write block processing reported is waiting.</summary>
    public bool HasWork
    {
        get
        {
            if (!_reported.IsEmpty) return true;
            lock (_lock) return _invalidated.Count > 0 && _refreshesRun < MaxRefreshesPerBlock;
        }
    }

    public bool WarmPassEnded => _warmPassEnded;

    public void EndWarmPass()
    {
        _warmPassEnded = true;
        _changed.Release();
    }

    /// <summary>Whether the refresh worker waits for the writes block processing reports once it has nothing left.</summary>
    public bool WaitsForBlockProcessing => _waitsForBlockProcessing;

    /// <summary>For a pass waited on before block processing: the refresh worker ends once it has nothing left.</summary>
    public void StopWaitingForBlockProcessing()
    {
        _waitsForBlockProcessing = false;
        _changed.Release();
    }

    /// <summary>
    /// Waits until a footprint is invalidated, block processing reports writes, the warm pass ends or the pass stops
    /// waiting for block processing.
    /// </summary>
    public void WaitForWork(CancellationToken token)
    {
        try
        {
            _changed.Wait(token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Queues the storage writes block processing committed when it executed the transaction at <paramref name="position"/>;
    /// null when it wrote none.
    /// </summary>
    public void QueueExecuted(int position, List<(StorageCell Cell, UInt256 Value)>? writes)
    {
        _reported.Enqueue((position, writes));
        _changed.Release();
    }

    /// <summary>Puts the writes block processing reported in place of what their footprints predicted, in the order it executed them.</summary>
    public void ApplyExecuted()
    {
        while (_reported.TryDequeue(out (int Position, List<(StorageCell Cell, UInt256 Value)>? Writes) executed))
        {
            ApplyExecuted(executed.Position, executed.Writes);
        }
    }

    private void ApplyExecuted(int position, List<(StorageCell Cell, UInt256 Value)>? writes)
    {
        if ((uint)position >= (uint)_footprints.Length) return;
        bool invalidated = false;
        lock (_lock)
        {
            // The transaction is done: its reads no longer invalidate it, and its writes are the ones it made.
            _executed[position] = true;
            StorageCell[]? predicted = Unindex(position);
            List<StorageCell> written = new(writes?.Count ?? 0);
            if (writes is not null)
            {
                foreach ((StorageCell cell, UInt256 value) in writes)
                {
                    if (WritesOf(in cell) is not { } versions) continue;
                    Insert(versions, position, in value);
                    written.Add(cell);
                }
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
            // A run that ends after block processing executed its transaction does not replace the writes it made.
            if (_executed[position]) return;

            // A refreshed footprint replaces what the previous one wrote and read.
            StorageCell[]? previous = Unindex(position);
            List<StorageCell> written = new(writes?.Count ?? 0);
            if (writes is not null)
            {
                foreach ((StorageCell cell, UInt256 value) in writes)
                {
                    if (WritesOf(in cell) is not { } versions) continue;
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
                outdated |= ValueBeforeLocked(in slot.Cell, position) is { } value && value != slot.Value;
                if (ReadersOf(in slot.Cell) is not { } readers) continue;
                readers.Add((position, slot.Value));
                read.Add(slot.Cell);
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

    // The slot's writes, created on first use; null for a slot too many footprints write, which is then no longer
    // tracked: its writes and its readers leave the index.
    private List<(int Position, UInt256 Value)>? WritesOf(in StorageCell cell)
    {
        if (_untracked.Contains(cell)) return null;
        if (!_writes.TryGetValue(cell, out List<(int Position, UInt256 Value)>? versions))
        {
            versions = [];
            _writes[cell] = versions;
            return versions;
        }

        if (versions.Count < MaxIndexedPerSlot) return versions;
        _untracked.Add(cell);
        _writes.Remove(cell);
        _reads.Remove(cell);
        return null;
    }

    // The slot's readers, created on first use; null for a slot no longer tracked, or one with as many readers as are
    // indexed, whose writes stay tracked.
    private List<(int Position, UInt256 Value)>? ReadersOf(in StorageCell cell)
    {
        if (_untracked.Contains(cell)) return null;
        if (!_reads.TryGetValue(cell, out List<(int Position, UInt256 Value)>? readers))
        {
            readers = [];
            _reads[cell] = readers;
            return readers;
        }

        return readers.Count < MaxIndexedPerSlot ? readers : null;
    }

    // Marks the later footprints that read the slot at a value other than the one the block now leaves before them. With
    // no earlier write left, the slot is back at its parent value, which only a refresh reads: its readers are marked.
    private bool InvalidateReaders(in StorageCell cell, int position)
    {
        if (!_reads.TryGetValue(cell, out List<(int Position, UInt256 Value)>? readers)) return false;
        bool invalidated = false;
        foreach ((int reader, UInt256 read) in readers)
        {
            if (reader > position && (ValueBeforeLocked(in cell, reader) is not { } value || value != read)) invalidated |= _invalidated.Add(reader);
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
