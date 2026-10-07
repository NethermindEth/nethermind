// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    // The writes and reads of each slot point into what is indexed at their position: the footprint stored there, or the
    // writes block processing reported for it, so indexing a footprint copies none of it.
    private readonly Dictionary<StorageCell, Slot> _slots = [];
    private readonly object?[] _indexed = new object?[block.Transactions.Length];
    private readonly bool[] _executed = new bool[block.Transactions.Length];
    private readonly ulong[] _invalidated = new ulong[(block.Transactions.Length + 63) >> 6];
    private int _invalidatedCount;
    private int _untrackedCount;
    private int _refreshesRun;
    private int _writesVersion;

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

    /// <param name="seededAt">For a refreshed footprint, the <see cref="WritesVersion"/> its run was seeded at.</param>
    public void Store(int index, TransactionFootprint footprint, int seededAt = -1)
    {
        Volatile.Write(ref _footprints[index], footprint);
        Index(index, footprint, seededAt);
    }

    /// <summary>Changes whenever writes the footprints leave are replaced.</summary>
    public int WritesVersion => Volatile.Read(ref _writesVersion);

    /// <summary>Refreshes the block has run.</summary>
    public int RefreshesRun
    {
        get
        {
            lock (_lock) return _refreshesRun;
        }
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
        lock (_lock)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
            return Unsafe.IsNullRef(ref slot) || slot.Untracked ? null : ValueBefore(in slot.Writers, position);
        }
    }

    /// <summary>Whether the footprint read a slot too many transactions write for its writes to be tracked.</summary>
    public bool ReadsUntrackedSlot(TransactionFootprint footprint)
    {
        lock (_lock)
        {
            if (_untrackedCount == 0) return false;
            foreach (ref readonly SlotPrecondition read in footprint.Slots)
            {
                ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, read.Cell);
                if (!Unsafe.IsNullRef(ref slot) && slot.Untracked) return true;
            }

            return false;
        }
    }

    /// <summary>Takes the first invalidated position after <paramref name="after"/>, dropping the ones before it.</summary>
    public bool TryTakeInvalidated(int after, out int position)
    {
        lock (_lock)
        {
            if (_invalidatedCount > 0 && _refreshesRun < MaxRefreshesPerBlock)
            {
                int first = after + 1;
                int word = first >> 6;
                for (int i = 0; i < word && i < _invalidated.Length; i++) Drop(i, ulong.MaxValue);
                if (word < _invalidated.Length) Drop(word, (1UL << (first & 63)) - 1);
                for (; word < _invalidated.Length; word++)
                {
                    if (_invalidated[word] == 0) continue;
                    position = (word << 6) + BitOperations.TrailingZeroCount(_invalidated[word]);
                    _invalidated[word] &= _invalidated[word] - 1;
                    _invalidatedCount--;
                    return true;
                }
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
            lock (_lock) return _invalidatedCount > 0 && _refreshesRun < MaxRefreshesPerBlock;
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
    public void WaitForWork(CancellationToken token) => _changed.Wait(token);

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
        bool invalidated;
        lock (_lock)
        {
            // The transaction is done: its reads no longer invalidate it, and its writes are the ones it made.
            _executed[position] = true;
            invalidated = Replace(position, writes);
        }

        if (invalidated) _changed.Release();
    }

    private void Index(int position, TransactionFootprint footprint, int seededAt)
    {
        bool invalidated;
        lock (_lock)
        {
            // A run that ends after block processing executed its transaction does not replace the writes it made.
            if (_executed[position]) return;
            // With no write replaced since the refresh seeded the slots the footprint before it read, a refreshed run
            // that read one of them at another value than its seed changed it itself first (a creation clears the
            // storage), and running it again reads the same.
            TransactionFootprint? seeded = seededAt == _writesVersion ? _indexed[position] as TransactionFootprint : null;
            invalidated = Replace(position, footprint);

            ReadOnlySpan<SlotPrecondition> reads = footprint.Slots;
            bool outdated = false;
            for (int i = 0; i < reads.Length; i++)
            {
                ref Slot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, reads[i].Cell, out _);
                if (slot.Untracked) continue;
                outdated |= ValueBefore(in slot.Writers, position) is { } value && value != reads[i].Value && (seeded is null || !Reads(seeded, reads[i].Cell));
                if (slot.Readers.Count < MaxIndexedPerSlot) slot.Readers.Add(new Entry(position, i));
            }

            if (outdated) invalidated |= Invalidate(position);
        }

        if (invalidated) _changed.Release();
    }

    // Puts what the position now writes, from a refreshed footprint or the transaction executed there, in place of what
    // was indexed for it, and marks the later readers of the slots either writes.
    private bool Replace(int position, object? source)
    {
        object? previous = _indexed[position];
        if (previous is not null) Unindex(position, previous);
        _indexed[position] = source;
        _writesVersion++;
        foreach ((StorageCell cell, int index) in WritesOf(source))
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, cell, out _);
            if (!slot.Untracked && !slot.Writers.Put(new Entry(position, index), MaxIndexedPerSlot)) Untrack(ref slot);
        }

        bool invalidated = false;
        foreach ((StorageCell cell, _) in WritesOf(source)) invalidated |= InvalidateReaders(in cell, position);
        foreach ((StorageCell cell, _) in WritesOf(previous)) invalidated |= InvalidateReaders(in cell, position);
        return invalidated;
    }

    private void Unindex(int position, object previous)
    {
        foreach ((StorageCell cell, _) in WritesOf(previous))
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
            if (!Unsafe.IsNullRef(ref slot)) slot.Writers.Remove(position);
        }

        if (previous is not TransactionFootprint footprint) return;
        foreach (ref readonly SlotPrecondition read in footprint.Slots)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, read.Cell);
            if (!Unsafe.IsNullRef(ref slot)) slot.Readers.Remove(position);
        }
    }

    // A slot too many footprints write is no longer tracked: its writes and its readers leave the index.
    private void Untrack(ref Slot slot)
    {
        slot.Untracked = true;
        slot.Writers.Clear();
        slot.Readers.Clear();
        _untrackedCount++;
    }

    // Marks the later footprints that read the slot at a value other than the one the block now leaves before them. With
    // no earlier write left, the slot is back at its parent value, which only a refresh reads: its readers are marked.
    private bool InvalidateReaders(in StorageCell cell, int position)
    {
        ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
        if (Unsafe.IsNullRef(ref slot)) return false;
        bool invalidated = false;
        for (int i = 0; i < slot.Readers.Count; i++)
        {
            Entry reader = slot.Readers[i];
            if (reader.Position > position && (ValueBefore(in slot.Writers, reader.Position) is not { } value || value != ReadValue(reader)))
            {
                invalidated |= Invalidate(reader.Position);
            }
        }

        return invalidated;
    }

    private bool Invalidate(int position)
    {
        ref ulong word = ref _invalidated[position >> 6];
        ulong bit = 1UL << (position & 63);
        if ((word & bit) != 0) return false;
        word |= bit;
        _invalidatedCount++;
        return true;
    }

    private void Drop(int word, ulong mask)
    {
        ulong dropped = _invalidated[word] & mask;
        _invalidated[word] &= ~dropped;
        _invalidatedCount -= BitOperations.PopCount(dropped);
    }

    private UInt256? ValueBefore(in Entries writers, int position)
    {
        int at = writers.FirstAtOrAfter(position);
        return at == 0 ? null : WrittenValue(writers[at - 1]);
    }

    private UInt256 WrittenValue(Entry writer) => _indexed[writer.Position] switch
    {
        TransactionFootprint footprint => footprint.Effects[writer.Index].Value,
        List<(StorageCell Cell, UInt256 Value)> writes => writes[writer.Index].Value,
        _ => throw new UnreachableException()
    };

    private UInt256 ReadValue(Entry reader) => ((TransactionFootprint)_indexed[reader.Position]!).Slots[reader.Index].Value;

    private static WrittenSlots WritesOf(object? source) => new(source);

    private static bool Reads(TransactionFootprint footprint, in StorageCell cell)
    {
        foreach (ref readonly SlotPrecondition read in footprint.Slots)
        {
            if (read.Cell.Equals(cell)) return true;
        }

        return false;
    }

    // A position, and where in what is indexed there the slot's write or read is.
    private readonly record struct Entry(int Position, int Index);

    private struct Slot
    {
        public Entries Writers;
        public Entries Readers;
        public bool Untracked;
    }

    // One slot's writers, in ascending position, or its readers: inline while there is one, in a list from the second.
    private struct Entries
    {
        private Entry _one;
        private List<Entry>? _many;
        private int _count;

        public readonly int Count => _count;

        public readonly Entry this[int i] => _many is null ? _one : _many[i];

        public void Add(Entry entry)
        {
            if (_many is not null) _many.Add(entry);
            else if (_count == 0) _one = entry;
            else _many = [_one, entry];
            _count++;
        }

        // Keeps ascending position and one entry per position; false when as many entries as allowed are taken.
        public bool Put(Entry entry, int capacity)
        {
            int at = FirstAtOrAfter(entry.Position);
            if (at < _count && this[at].Position == entry.Position)
            {
                if (_many is null) _one = entry;
                else _many[at] = entry;
                return true;
            }

            if (_count >= capacity) return false;
            if (_many is not null) _many.Insert(at, entry);
            else if (_count == 0) _one = entry;
            else _many = at == 0 ? [entry, _one] : [_one, entry];
            _count++;
            return true;
        }

        public void Remove(int position)
        {
            if (_many is null)
            {
                if (_count == 1 && _one.Position == position) _count = 0;
                return;
            }

            for (int i = _many.Count - 1; i >= 0; i--)
            {
                if (_many[i].Position == position) _many.RemoveAt(i);
            }

            _count = _many.Count;
        }

        public void Clear()
        {
            _many = null;
            _count = 0;
        }

        public readonly int FirstAtOrAfter(int position)
        {
            int low = 0, high = _count;
            while (low < high)
            {
                int mid = (low + high) >>> 1;
                if (this[mid].Position < position) low = mid + 1;
                else high = mid;
            }

            return low;
        }
    }

    // The slots a footprint's storage effects or reported writes write, with where each write is in them.
    private struct WrittenSlots(object? source)
    {
        private int _index = -1;

        public (StorageCell Cell, int Index) Current { get; private set; }

        public readonly WrittenSlots GetEnumerator() => this;

        public bool MoveNext()
        {
            switch (source)
            {
                case TransactionFootprint footprint:
                    ReadOnlySpan<StateEffect> effects = footprint.Effects;
                    while (++_index < effects.Length)
                    {
                        ref readonly StateEffect effect = ref effects[_index];
                        if (effect.Kind != EffectKind.SetStorage) continue;
                        Current = (new StorageCell(effect.Address, in effect.Index), _index);
                        return true;
                    }

                    return false;
                case List<(StorageCell Cell, UInt256 Value)> writes:
                    if (++_index >= writes.Count) return false;
                    Current = (writes[_index].Cell, _index);
                    return true;
                default:
                    return false;
            }
        }
    }
}
