// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// <param name="refreshWorkers">The workers that refresh the block's footprints side by side.</param>
internal sealed class BlockFootprints(Block block, int refreshWorkers = 1)
{
    /// <summary>
    /// Footprints indexed for one slot's writes, and for its reads. Past it, a slot's writes are no longer tracked and the
    /// footprints that read it are not refreshed; a reader past it is only not invalidated through that slot.
    /// </summary>
    internal const int MaxIndexedPerSlot = 64;

    /// <summary>Refreshes one block may run.</summary>
    internal const int MaxRefreshesPerBlock = 128;

    // Writes times effects past which block processing wakes a refresh worker rather than compare: comparing would
    // cost more than the wake.
    private const int MaxComparisons = 256;

    // Bound the look at a candidate's writers under the lock: the slots of a candidate checked, and the candidates passed
    // over in one take.
    private const int MaxWriterChecks = 64;
    private const int MaxPassedOver = 16;

    private readonly Hash256? _blockHash = block.Hash;
    private readonly TransactionFootprint?[] _footprints = new TransactionFootprint?[block.Transactions.Length];

    private readonly Lock _lock = new();
    // The writes and reads of each slot point into what is indexed at their position: the footprint stored there, or the
    // writes block processing reported for it, so indexing a footprint copies none of it.
    private readonly Dictionary<StorageCell, Slot> _slots = [];
    private readonly Indexed[] _indexed = new Indexed[block.Transactions.Length];
    private readonly bool[] _executed = new bool[block.Transactions.Length];
    private readonly ulong[] _invalidated = new ulong[(block.Transactions.Length + 63) >> 6];
    // The take of the refresh each position last stored the footprint of; 0 while none has.
    private readonly int[] _storedTakes = new int[block.Transactions.Length];
    // The refreshes of each position taken and not yet ended, and of all positions.
    private readonly int[] _refreshing = new int[block.Transactions.Length];
    private int _inFlight;
    private int _invalidatedCount;
    private int _untrackedCount;
    private int _refreshesRun;
    private int _takes;
    private int _writesVersion;
    private int _visit;

    // Written by block processing, applied by the refresh workers, so block processing never takes the lock.
    private readonly ConcurrentQueue<(int Position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes)> _reported = new();
    private readonly SemaphoreSlim _changed = new(0);
    private readonly int _refreshWorkers = Math.Max(1, refreshWorkers);
    private volatile bool _warmPassEnded;
    private volatile bool _waitsForBlockProcessing = true;

    /// <remarks>
    /// Needs receipts without a per-transaction state root (EIP-658). EIP-8037 gas accounting and a block access list
    /// (EIP-7928) are built while transactions execute, which a replay does not.
    /// </remarks>
    public static bool AppliesTo(Block block, IReleaseSpec spec) =>
        block.Transactions.Length > 0 && AppliesTo(spec) && block.BlockAccessList is null;

    /// <inheritdoc cref="AppliesTo(Block, IReleaseSpec)"/>
    public static bool AppliesTo(IReleaseSpec spec) =>
        spec.IsEip658Enabled && !spec.IsEip8037Enabled && !spec.BlockLevelAccessListsEnabled;

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

    /// <param name="take">For a refreshed footprint, the take of the refresh from <see cref="TryTakeInvalidated"/>.</param>
    /// <param name="seeds">For a refreshed footprint, the slots its run was seeded with, at their seeds.</param>
    /// <returns>False when a refresh of the position taken later has stored its footprint already; nothing is stored then.</returns>
    public bool Store(int index, TransactionFootprint footprint, int take = 0, ReadOnlySpan<(StorageCell Cell, UInt256 Value)> seeds = default)
    {
        bool invalidated;
        lock (_lock)
        {
            if (take != 0)
            {
                // A position invalidated again while it is refreshed can be taken and refreshed again side by side: the
                // run taken first, seeded on the older values, must not replace what the run taken later stored.
                if (take < _storedTakes[index]) return false;
                _storedTakes[index] = take;
            }

            // Stored and indexed under one lock, so two stores of a position cannot leave block processing one footprint
            // and the index another.
            Volatile.Write(ref _footprints[index], footprint);
            invalidated = Index(index, footprint, seeds);
        }

        if (invalidated) _changed.Release();
        return true;
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

    /// <summary>
    /// Takes the first invalidated position after <paramref name="after"/> whose seeds are settled, dropping the ones
    /// before it.
    /// </summary>
    /// <remarks>
    /// A position is passed over, and stays invalidated, while a slot it read takes its value from the write of an earlier
    /// position that is being refreshed or is invalidated: seeded now, it would run again once that write changes. With one
    /// refresh worker nothing is refreshed while it takes, and the invalidated positions before the first were taken or
    /// dropped, so the first is taken as it always was. With more than one, a take that leaves invalidated positions wakes
    /// another worker, which takes the first settled one left: the workers refresh side by side, nearest to block
    /// processing first.
    /// </remarks>
    /// <param name="take">Orders the block's refreshes: a refresh taken later has a larger one. Each take is ended with <see cref="EndRefresh"/>.</param>
    public bool TryTakeInvalidated(int after, out int position, out int take)
    {
        lock (_lock)
        {
            if (_invalidatedCount > 0 && _refreshesRun < MaxRefreshesPerBlock)
            {
                int first = after + 1;
                int word = first >> 6;
                for (int i = 0; i < word && i < _invalidated.Length; i++) Drop(i, ulong.MaxValue);
                if (word < _invalidated.Length) Drop(word, (1UL << (first & 63)) - 1);
                int settled = FirstSettled(word);
                if (settled >= 0)
                {
                    position = settled;
                    _invalidated[settled >> 6] &= ~(1UL << (settled & 63));
                    _invalidatedCount--;
                    take = ++_takes;
                    _refreshing[settled]++;
                    _inFlight++;
                    if (_refreshWorkers > 1 && _invalidatedCount > 0) WakeAnother();
                    return true;
                }
            }
        }

        position = -1;
        take = 0;
        return false;
    }

    /// <summary>Ends a refresh taken by <see cref="TryTakeInvalidated"/>, whatever came of it.</summary>
    public void EndRefresh(int position)
    {
        lock (_lock)
        {
            _refreshing[position]--;
            _inFlight--;
        }
    }

    // Under the lock: the first invalidated position from the word on whose seeds are settled, or -1. Only a refresh in
    // flight holds the first invalidated position back, and its worker takes again as it ends, so passing over a few is
    // enough.
    private int FirstSettled(int word)
    {
        int passedOver = 0;
        for (; word < _invalidated.Length; word++)
        {
            for (ulong left = _invalidated[word]; left != 0; left &= left - 1)
            {
                int candidate = (word << 6) + BitOperations.TrailingZeroCount(left);
                if (_inFlight == 0 || !WaitsForWriter(candidate)) return candidate;
                if (++passedOver == MaxPassedOver) return -1;
            }
        }

        return -1;
    }

    // Under the lock: whether a slot the candidate's footprint read takes its value from the write of an earlier position
    // that is being refreshed or is invalidated, so the seed a refresh would give it is about to change.
    private bool WaitsForWriter(int candidate)
    {
        if (_indexed[candidate].Footprint is not { } footprint) return false;
        ReadOnlySpan<SlotPrecondition> reads = footprint.Slots;
        for (int i = 0; i < reads.Length && i < MaxWriterChecks; i++)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, reads[i].Cell);
            if (Unsafe.IsNullRef(ref slot) || slot.Untracked) continue;
            int at = slot.Writers.FirstAtOrAfter(candidate);
            if (at == 0) continue;
            int writer = slot.Writers[at - 1].Position;
            if (_refreshing[writer] > 0 || (_invalidated[writer >> 6] & (1UL << (writer & 63))) != 0) return true;
        }

        return false;
    }

    // Keeps no more wakes than there are other workers: more would only wake them again once everything is taken.
    private void WakeAnother()
    {
        if (_changed.CurrentCount < _refreshWorkers - 1) _changed.Release();
    }

    /// <summary>Counts a refresh that runs; false once the block has run its refreshes, when it must not run.</summary>
    /// <remarks>Workers that took their positions while the block had refreshes left can come to count them after it has none.</remarks>
    public bool TryCountRefresh()
    {
        lock (_lock)
        {
            if (_refreshesRun >= MaxRefreshesPerBlock) return false;
            _refreshesRun++;
            return true;
        }
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

    /// <remarks>
    /// Releases a wake for each refresh worker. Wakes go to whichever worker waits, not to each one, so it is
    /// <see cref="Leave"/> that has every worker check whether it is done.
    /// </remarks>
    public void EndWarmPass()
    {
        _warmPassEnded = true;
        _changed.Release(_refreshWorkers);
    }

    /// <summary>Whether the refresh workers wait for the writes block processing reports once they have nothing left.</summary>
    public bool WaitsForBlockProcessing => _waitsForBlockProcessing;

    /// <summary>For a pass waited on before block processing: each refresh worker ends once it has nothing left.</summary>
    /// <remarks>
    /// Releases a wake for each refresh worker. Wakes go to whichever worker waits, not to each one, so it is
    /// <see cref="Leave"/> that has every worker check whether it is done.
    /// </remarks>
    public void StopWaitingForBlockProcessing()
    {
        _waitsForBlockProcessing = false;
        _changed.Release(_refreshWorkers);
    }

    /// <summary>
    /// Whether a refresh worker that found nothing to take is done: block processing has started the block's last
    /// transaction, or a pass waited on before block processing has nothing left.
    /// </summary>
    /// <param name="processing">The transaction block processing has started; -1 before it starts.</param>
    public bool IsDone(int processing) =>
        processing >= _footprints.Length - 1 || (processing < 0 && !_waitsForBlockProcessing && _warmPassEnded && !HasWork);

    /// <summary>A refresh worker leaves, whichever way: it wakes the next, which checks in turn whether it is done.</summary>
    /// <remarks>
    /// Wakes go to whichever worker waits, not to each one. A worker that takes a wake and waits again, because another
    /// worker's store left work it then took itself, uses up a wake another worker needed to see the pass end; a worker
    /// that leaves on an error leaves the positions its refreshes held back to the others. With one worker there is no
    /// other to wake.
    /// </remarks>
    public void Leave()
    {
        if (_refreshWorkers > 1) _changed.Release();
    }

    /// <summary>
    /// Waits until a footprint is invalidated, block processing reports writes, the warm pass ends or the pass stops
    /// waiting for block processing.
    /// </summary>
    public void WaitForWork(CancellationToken token) => _changed.Wait(token);

    /// <summary>For tests: the wakes the refresh workers have not taken.</summary>
    internal int PendingWakes => _changed.CurrentCount;

    /// <summary>
    /// Queues the storage writes block processing committed when it executed the transaction at <paramref name="position"/>;
    /// empty when it wrote none.
    /// </summary>
    /// <remarks>
    /// Writes its footprint predicted invalidate nothing, so they wait for a refresh worker's next wake: waking a
    /// blocked worker costs block processing microseconds.
    /// </remarks>
    public void QueueExecuted(int position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes)
    {
        _reported.Enqueue((position, writes));
        if (!WerePredicted(position, writes)) _changed.Release();
    }

    [SkipLocalsInit]
    private bool WerePredicted(int position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes)
    {
        TransactionFootprint?[] footprints = _footprints;
        TransactionFootprint? footprint = (uint)position < (uint)footprints.Length ? Volatile.Read(ref footprints[position]) : null;
        int count = writes.Length;
        if ((footprint?.StorageWrites ?? 0) != count) return false;
        if (count == 0) return true;
        ReadOnlySpan<StateEffect> effects = footprint!.Effects;
        if (count * effects.Length > MaxComparisons) return false;
        // A footprint writes each slot once, and a commit reports each slot once.
        foreach ((StorageCell cell, UInt256 value) in writes.Span)
        {
            if (!Writes(effects, in cell, in value)) return false;
        }

        return true;
    }

    private static bool Writes(ReadOnlySpan<StateEffect> effects, in StorageCell cell, in UInt256 value)
    {
        foreach (ref readonly StateEffect effect in effects)
        {
            if (effect.Kind == EffectKind.SetStorage && effect.Value == value && effect.Cell.Equals(in cell)) return true;
        }

        return false;
    }

    /// <summary>Puts the writes block processing reported in place of what their footprints predicted, in the order it executed them.</summary>
    /// <remarks>
    /// Refresh workers that apply them side by side can put two transactions' writes in the other order. That leaves the
    /// same writes, and every footprint they leave invalidated is invalidated either way: each write checks the readers
    /// after it against what the block then leaves before them.
    /// </remarks>
    public void ApplyExecuted()
    {
        while (_reported.TryDequeue(out (int Position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes) executed))
        {
            ApplyExecuted(executed.Position, executed.Writes);
        }
    }

    private void ApplyExecuted(int position, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> writes)
    {
        if ((uint)position >= (uint)_footprints.Length) return;
        bool invalidated;
        lock (_lock)
        {
            // The transaction is done: its reads no longer invalidate it, and its writes are the ones it made.
            _executed[position] = true;
            invalidated = Replace(position, new Indexed(null, writes));
        }

        if (invalidated) _changed.Release();
    }

    // Under the lock; whether it invalidated a footprint.
    private bool Index(int position, TransactionFootprint footprint, ReadOnlySpan<(StorageCell Cell, UInt256 Value)> seeds)
    {
        // A run that ends after block processing executed its transaction does not replace the writes it made.
        if (_executed[position]) return false;
        bool invalidated = Replace(position, new Indexed(footprint, default));

        // A refreshed run that read a slot it was seeded with at another value than its seed changed the slot itself first
        // (a creation clears the storage), and running it again reads the same while the footprints before it still leave
        // the seed there.
        ReadOnlySpan<SlotPrecondition> reads = footprint.Slots;
        bool outdated = false;
        for (int i = 0; i < reads.Length; i++)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, reads[i].Cell, out _);
            if (slot.Untracked) continue;
            outdated |= ValueBefore(in slot.Writers, position) is { } value && value != reads[i].Value && !Seeded(seeds, reads[i].Cell, value);
            if (slot.Readers.Count < MaxIndexedPerSlot) slot.Readers.Add(new Entry(position, i));
        }

        if (outdated) invalidated |= Invalidate(position);
        return invalidated;
    }

    // Puts what the position now writes, from a refreshed footprint or the transaction executed there, in place of what
    // was indexed for it, and marks the later readers of the slots either writes. A slot written many times is handled
    // once per pass; its entry keeps the last write.
    private bool Replace(int position, in Indexed source)
    {
        Indexed previous = _indexed[position];
        Unindex(position, in previous);
        _indexed[position] = source;
        _writesVersion++;
        foreach ((StorageCell cell, int index) in source.Writes)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, cell, out _);
            if (!slot.Untracked && !slot.Writers.Put(new Entry(position, index), MaxIndexedPerSlot)) Untrack(ref slot);
        }

        int visit = ++_visit;
        bool invalidated = false;
        foreach ((StorageCell cell, _) in source.Writes) invalidated |= InvalidateReaders(in cell, position, visit);
        foreach ((StorageCell cell, _) in previous.Writes) invalidated |= InvalidateReaders(in cell, position, visit);
        return invalidated;
    }

    private void Unindex(int position, in Indexed previous)
    {
        int visit = ++_visit;
        foreach ((StorageCell cell, _) in previous.Writes)
        {
            ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
            if (Unsafe.IsNullRef(ref slot) || slot.Visited == visit) continue;
            slot.Visited = visit;
            slot.Writers.Remove(position);
        }

        if (previous.Footprint is not { } footprint) return;
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
    private bool InvalidateReaders(in StorageCell cell, int position, int visit)
    {
        ref Slot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_slots, cell);
        if (Unsafe.IsNullRef(ref slot) || slot.Visited == visit) return false;
        slot.Visited = visit;
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

    private UInt256 WrittenValue(Entry writer) => _indexed[writer.Position].WrittenValue(writer.Index);

    // A reader's position always has its footprint indexed: executed positions keep no readers.
    private UInt256 ReadValue(Entry reader) => _indexed[reader.Position].Footprint!.Slots[reader.Index].Value;

    // Whether the run was seeded with the value the footprints before its position now leave in the slot.
    private static bool Seeded(ReadOnlySpan<(StorageCell Cell, UInt256 Value)> seeds, in StorageCell cell, in UInt256 value)
    {
        foreach (ref readonly (StorageCell Cell, UInt256 Value) seed in seeds)
        {
            if (seed.Value == value && seed.Cell.Equals(cell)) return true;
        }

        return false;
    }

    // A position, and where in what is indexed there the slot's write or read is.
    private readonly record struct Entry(int Position, int Index);

    // What is indexed at a position: the footprint stored there, or the writes block processing reported for it.
    private readonly struct Indexed(TransactionFootprint? footprint, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> executed)
    {
        public TransactionFootprint? Footprint => footprint;

        public WrittenSlots Writes => new(footprint, executed);

        public UInt256 WrittenValue(int index) => footprint is not null ? footprint.Effects[index].Value : executed.Span[index].Value;
    }

    private struct Slot
    {
        public Entries Writers;
        public Entries Readers;
        public bool Untracked;
        // The pass of Replace that last handled the slot.
        public int Visited;
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
    private struct WrittenSlots(TransactionFootprint? footprint, ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> executed)
    {
        private int _index = -1;

        public (StorageCell Cell, int Index) Current { get; private set; }

        public readonly WrittenSlots GetEnumerator() => this;

        public bool MoveNext()
        {
            if (footprint is not null)
            {
                ReadOnlySpan<StateEffect> effects = footprint.Effects;
                while (++_index < effects.Length)
                {
                    ref readonly StateEffect effect = ref effects[_index];
                    if (effect.Kind != EffectKind.SetStorage) continue;
                    Current = (effect.Cell, _index);
                    return true;
                }

                return false;
            }

            if (++_index >= executed.Length) return false;
            Current = (executed.Span[_index].Cell, _index);
            return true;
        }
    }
}
