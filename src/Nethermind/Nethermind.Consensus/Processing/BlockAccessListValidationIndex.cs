// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

// Column-oriented snapshot of a BlockAccessList used for fast equality checks
// during incremental block validation. Each lane (balance/nonce/code/storage)
// stores per-row (per-tx-index) tuples sorted by (accountOrdinal, key) so two
// indexes can be compared row-by-row in bulk without per-account dictionary
// lookups. Two construction modes:
//   * Build(...): immutable index built once from the suggested BAL. Lanes are
//     pre-sized via Counts and sorted in place.
//   * ctor(txCount, addressIndex, suggested): mutable index that mirrors the
//     suggested layout; the generator pushes rows into it via Add(slice) as
//     each per-tx BlockAccessListAtIndex slice is merged into the cumulative
//     GeneratedBlockAccessList. ChangesEqual(...) then compares row-by-row
//     against the suggested index. _hasOutOfRangeChange flips when a generated
//     change targets an index past the suggested last index — in that case
//     full-slow validation is required.
internal sealed partial class BlockAccessListValidationIndex : IDisposable
{
    private readonly AddressIndex _addressIndex;
    private readonly uint _lastIndex;
    private readonly bool _isMutable;
    private readonly LaneStore _lanes;
    private ulong[] _hasAccountWords;
    private bool _disposed;
    private bool _hasOutOfRangeChange;
    // Generated-side accumulators (mutable index only). Flat (ordinal, slot) lists sorted
    // lazily on first structural-equivalence query. Writes mirror StorageChanges and gate
    // the wire BAL's read→write promotion when comparing StorageReads.
    private readonly List<(int Ordinal, UInt256 Slot)>? _generatedStorageReads;
    private bool _generatedStorageReadsSorted = true;
    private readonly List<(int Ordinal, UInt256 Slot)>? _generatedStorageWrites;
    private bool _generatedStorageWritesSorted = true;
    // First (row, lane) where Add overflowed: row capacity is sized to the suggested side,
    // so an overflow is itself a structural mismatch — HasAt would otherwise drop it.
    private Address? _generatedOverflowAddress;
    private uint _generatedOverflowIndex;

    public BlockAccessListValidationIndex(int txCount, AddressIndex addressIndex, BlockAccessListValidationIndex suggested, int storageReadsCapacity, int storageWritesCapacity)
    {
        _addressIndex = addressIndex;
        _lastIndex = GetLastIndex(txCount);
        if (_lastIndex != suggested._lastIndex)
            throw new ArgumentException("Suggested validation index has a different row count.", nameof(suggested));
        _isMutable = true;
        _lanes = new LaneStore(suggested._lanes);
        // Slack covers per-tx duplication before dedup on reads, and invalid wire BALs that push
        // generated past suggested before lane overflow trips.
        _generatedStorageReads = new(WithSlack(storageReadsCapacity));
        _generatedStorageWrites = new(WithSlack(storageWritesCapacity));
        // Start with an empty bitmap; MarkAccount grows it through the pool on first use, so
        // blocks that never call MarkAccount don't rent anything.
        _hasAccountWords = [];
    }

    /// <summary>Capacity hint plus headroom so the lists don't immediately resize on the first
    /// over-count slot.</summary>
    private const int GeneratedListSlackShift = 2;
    private static int WithSlack(int capacity) => capacity + (capacity >> GeneratedListSlackShift);

    private BlockAccessListValidationIndex(
        AddressIndex addressIndex,
        uint lastIndex,
        LaneStore lanes,
        ulong[] hasAccountWords)
    {
        _addressIndex = addressIndex;
        _lastIndex = lastIndex;
        _lanes = lanes;
        _hasAccountWords = hasAccountWords;
    }

    public static BlockAccessListValidationIndex Build(ReadOnlyBlockAccessList blockAccessList, int txCount, AddressIndex addressIndex)
    {
        uint lastIndex = GetLastIndex(txCount);
        int rowCount = checked((int)lastIndex + 1);
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = blockAccessList.AccountChanges.AsSpan();

        // One pooled buffer carved into four per-row counter spans, instead of four separate
        // rents — each lane occupies a contiguous rowCount-sized window.
        using ArrayPoolListRef<int> counters = new(rowCount * 4, rowCount * 4);
        LaneSpans counts = PartitionInQuarters(counters.AsSpan(), rowCount);

        Count(accounts, counts, lastIndex);

        LaneStore lanes = default;
        ulong[]? hasAccountWords = null;
        try
        {
            lanes = new LaneStore(counts);
            int wordCount = WordCount(accounts.Length);
            hasAccountWords = wordCount == 0 ? [] : PooledArrays.RentCleared<ulong>(wordCount);
            lanes.FillFromAccounts(accounts, addressIndex, lastIndex, hasAccountWords);
            lanes.SortAllRows();
            return new(addressIndex, lastIndex, lanes, hasAccountWords);
        }
        catch
        {
            if (hasAccountWords is { Length: > 0 }) PooledArrays.Return(hasAccountWords);
            lanes.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Append a per-tx generated slice into the mutable validation index. Called from
    /// <c>BlockAccessListManager.MergeAndReturnBal</c> after each tx's
    /// <see cref="BlockAccessListAtIndex"/> is folded into the cumulative
    /// <see cref="GeneratedBlockAccessList"/>; pushes exactly the rows that landed at the
    /// slice's tx index so subsequent ChangesEqual(index) calls compare against suggested.
    /// </summary>
    public void Add(BlockAccessListAtIndex slice)
    {
        if (!_isMutable)
            throw new InvalidOperationException("Only generated validation indexes can be appended.");

        foreach (AccountChangesAtIndex accountChanges in slice.AccountChanges)
        {
            int accountOrdinal = _addressIndex.GetOrAdd(accountChanges.Address);
            MarkAccount(ref _hasAccountWords, accountOrdinal);

            if (accountChanges.BalanceChange is { } balance)
            {
                if (TryGetRow(balance.Index, _lastIndex, out int row)) RecordIfOverflow(_lanes.TryAddBalance(row, accountOrdinal, balance.Value), balance.Index, accountChanges.Address);
                else _hasOutOfRangeChange = true;
            }

            if (accountChanges.NonceChange is { } nonce)
            {
                if (TryGetRow(nonce.Index, _lastIndex, out int row)) RecordIfOverflow(_lanes.TryAddNonce(row, accountOrdinal, nonce.Value), nonce.Index, accountChanges.Address);
                else _hasOutOfRangeChange = true;
            }

            if (accountChanges.CodeChange is { } code)
            {
                if (TryGetRow(code.Index, _lastIndex, out int row)) RecordIfOverflow(_lanes.TryAddCode(row, accountOrdinal, code.CodeHash), code.Index, accountChanges.Address);
                else _hasOutOfRangeChange = true;
            }

            List<(int, UInt256)> writes = _generatedStorageWrites!;
            int writesCountBefore = writes.Count;
            foreach (KeyValuePair<UInt256, StorageChange> kv in accountChanges.StorageChanges)
            {
                if (TryGetRow(kv.Value.Index, _lastIndex, out int row)) RecordIfOverflow(_lanes.TryAddStorage(row, accountOrdinal, kv.Key, kv.Value.Value), kv.Value.Index, accountChanges.Address);
                else _hasOutOfRangeChange = true;
                writes.Add((accountOrdinal, kv.Key));
            }
            if (writes.Count != writesCountBefore) _generatedStorageWritesSorted = false;

            if (accountChanges.StorageReads.Count > 0)
            {
                List<(int, UInt256)> reads = _generatedStorageReads!;
                foreach (UInt256 read in accountChanges.StorageReads) reads.Add((accountOrdinal, read));
                _generatedStorageReadsSorted = false;
            }
        }

        _lanes.SortTouchedRows();
    }

    private void RecordIfOverflow(bool added, uint index, Address address)
    {
        if (added || _generatedOverflowAddress is not null) return;
        _generatedOverflowAddress = address;
        _generatedOverflowIndex = index;
    }

    /// <summary>
    /// If a lane Add overflowed during a prior <see cref="Add(BlockAccessListAtIndex)"/> call,
    /// returns the offending (address, index) pair. Overflow means generated produced a change
    /// at a (row, lane) that suggested didn't declare — a structural mismatch the slow path
    /// must surface even though <see cref="Lane{TValue}.HasAt"/> can't see the dropped row.
    /// </summary>
    public bool TryGetGeneratedOverflow(out Address address, out uint index)
    {
        if (_generatedOverflowAddress is null)
        {
            address = default!;
            index = 0;
            return false;
        }
        address = _generatedOverflowAddress;
        index = _generatedOverflowIndex;
        return true;
    }

    public bool HasAccount(Address address) =>
        _addressIndex.TryGet(address, out int accountOrdinal) &&
        HasAccountOrdinal(accountOrdinal);

    /// <summary>
    /// Number of accounts marked in this index's bitmap. Equivalent to the count of touched
    /// addresses on the generated side or declared addresses on the suggested side.
    /// </summary>
    public int MarkedAccountCount
    {
        get
        {
            int count = 0;
            ReadOnlySpan<ulong> words = _hasAccountWords;
            for (int i = 0; i < words.Length; i++) count += BitOperations.PopCount(words[i]);
            return count;
        }
    }

    public enum StructuralMismatchKind
    {
        None,
        AccountCountMismatch,
        MissingInGenerated,
        StorageReadsCountMismatch,
        StorageReadsContentMismatch,
    }

    /// <summary>
    /// Catches what the column-index <see cref="ChangesEqual"/> doesn't: account-set presence
    /// (via bitmap) and the storage_reads contents per account. Equivalent to re-encoding the
    /// generated BAL and comparing bytes to the wire hash, without the encode + Keccak pass.
    /// </summary>
    /// <remarks>
    /// Must only be called on the mutable (generated) index. <c>_generatedStorageReads</c> and
    /// <c>_generatedStorageWrites</c> are null on the immutable (suggested) side.
    /// </remarks>
    public StructuralMismatchKind FindStructuralMismatch(ReadOnlyBlockAccessList suggested, out Address? mismatchAddress, out int generatedAccountCount)
    {
        if (!_isMutable)
            throw new InvalidOperationException("FindStructuralMismatch must be called on the generated index.");

        mismatchAddress = null;
        generatedAccountCount = MarkedAccountCount;

        ReadOnlySpan<ReadOnlyAccountChanges> suggestedAccounts = suggested.AccountChanges.AsSpan();
        if (suggestedAccounts.Length != generatedAccountCount)
        {
            return StructuralMismatchKind.AccountCountMismatch;
        }

        // After sort+dedup each flat buffer is in (ordinal asc, slot asc) order, so per-ordinal
        // runs line up with suggested.AccountChanges' address-sorted iteration. Reads/writes
        // walk in lockstep per account, dropping reads whose slot also has a write — mirroring
        // GeneratedBlockAccessList.Merge's read→write promotion.
        SortAndDedupFlat(_generatedStorageReads, ref _generatedStorageReadsSorted);
        SortAndDedupFlat(_generatedStorageWrites, ref _generatedStorageWritesSorted);
        ReadOnlySpan<(int Ordinal, UInt256 Slot)> reads = _generatedStorageReads is null
            ? default
            : CollectionsMarshal.AsSpan(_generatedStorageReads);
        ReadOnlySpan<(int Ordinal, UInt256 Slot)> writes = _generatedStorageWrites is null
            ? default
            : CollectionsMarshal.AsSpan(_generatedStorageWrites);
        int readsCursor = 0;
        int writesCursor = 0;
        int lastOrdinal = -1;

        foreach (ReadOnlyAccountChanges suggestedAccount in suggestedAccounts)
        {
            if (!_addressIndex.TryGet(suggestedAccount.Address, out int ordinal) || !HasAccountOrdinal(ordinal))
            {
                mismatchAddress = suggestedAccount.Address;
                return StructuralMismatchKind.MissingInGenerated;
            }

            // suggested.AccountChanges is address-sorted and Build() assigns ordinals in that
            // iteration order, so ordinals here are monotonically increasing — the reads/writes
            // cursors below rely on this to avoid backtracking.
            Debug.Assert(ordinal > lastOrdinal, "AccountChanges enumeration must produce ordinals in ascending order.");
            lastOrdinal = ordinal;

            ReadOnlySpan<(int Ordinal, UInt256 Slot)> generatedReadsForOrdinal = TakeOrdinalRun(reads, ordinal, ref readsCursor);
            ReadOnlySpan<(int Ordinal, UInt256 Slot)> generatedWritesForOrdinal = TakeOrdinalRun(writes, ordinal, ref writesCursor);

            StructuralMismatchKind mismatch = CompareStorageReadsForAccount(
                generatedReadsForOrdinal, generatedWritesForOrdinal, suggestedAccount.StorageReads);
            if (mismatch != StructuralMismatchKind.None)
            {
                mismatchAddress = suggestedAccount.Address;
                return mismatch;
            }
        }

        return StructuralMismatchKind.None;
    }

    /// <summary>Returns the run of entries whose Ordinal equals <paramref name="ordinal"/>,
    /// advancing <paramref name="cursor"/> past it. Assumes <paramref name="entries"/> is
    /// sorted by Ordinal ascending and the caller calls in ascending-ordinal order.</summary>
    private static ReadOnlySpan<(int Ordinal, UInt256 Slot)> TakeOrdinalRun(
        ReadOnlySpan<(int Ordinal, UInt256 Slot)> entries, int ordinal, ref int cursor)
    {
        while (cursor < entries.Length && entries[cursor].Ordinal < ordinal) cursor++;
        int runStart = cursor;
        while (cursor < entries.Length && entries[cursor].Ordinal == ordinal) cursor++;
        return entries.Slice(runStart, cursor - runStart);
    }

    /// <summary>Compares generated reads (filtered for slots shadowed by writes) against
    /// suggested reads for a single account. Both sides are slot-sorted ascending.</summary>
    private static StructuralMismatchKind CompareStorageReadsForAccount(
        ReadOnlySpan<(int Ordinal, UInt256 Slot)> generatedReads,
        ReadOnlySpan<(int Ordinal, UInt256 Slot)> generatedWrites,
        ReadOnlySpan<UInt256> suggestedReads)
    {
        int writeIdx = 0;
        int suggestedIdx = 0;
        for (int readIdx = 0; readIdx < generatedReads.Length; readIdx++)
        {
            UInt256 slot = generatedReads[readIdx].Slot;
            while (writeIdx < generatedWrites.Length && generatedWrites[writeIdx].Slot.CompareTo(slot) < 0) writeIdx++;
            if (writeIdx < generatedWrites.Length && generatedWrites[writeIdx].Slot.Equals(slot)) continue;

            if (suggestedIdx >= suggestedReads.Length) return StructuralMismatchKind.StorageReadsCountMismatch;
            if (!suggestedReads[suggestedIdx].Equals(slot)) return StructuralMismatchKind.StorageReadsContentMismatch;
            suggestedIdx++;
        }
        return suggestedIdx == suggestedReads.Length ? StructuralMismatchKind.None : StructuralMismatchKind.StorageReadsCountMismatch;
    }

    private static void SortAndDedupFlat(List<(int Ordinal, UInt256 Slot)>? buffer, ref bool sorted)
    {
        if (sorted || buffer is null) return;

        Span<(int Ordinal, UInt256 Slot)> span = CollectionsMarshal.AsSpan(buffer);
        span.Sort(static (a, b) =>
        {
            int c = a.Ordinal.CompareTo(b.Ordinal);
            return c != 0 ? c : a.Slot.CompareTo(b.Slot);
        });

        int writeIdx = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (writeIdx == 0 || span[i].Ordinal != span[writeIdx - 1].Ordinal || !span[i].Slot.Equals(span[writeIdx - 1].Slot))
            {
                span[writeIdx++] = span[i];
            }
        }
        CollectionsMarshal.SetCount(buffer, writeIdx);

        sorted = true;
    }

    public bool ChangesEqual(BlockAccessListValidationIndex other, uint index)
    {
        if (index > _lastIndex || index > other._lastIndex || _hasOutOfRangeChange || other._hasOutOfRangeChange)
        {
            return false;
        }

        return _lanes.ChangesEqual(other._lanes, (int)index);
    }

    /// <summary>
    /// The bundled-lane storage. Exposed so the validation slow path in
    /// <c>BlockAccessListManager</c> can issue <see cref="LaneStore.HasAt"/> and
    /// <see cref="LaneStore.ChangesAtRowEqualForOrdinal"/> queries against the typed lanes
    /// without going through one-line wrappers on the index.
    /// </summary>
    internal LaneStore Lanes => _lanes;

    /// <summary>
    /// True iff this index has accumulated any storage_reads for the given ordinal.
    /// Sorts the flat list lazily on first call.
    /// </summary>
    public bool HasStorageReadsForOrdinal(int ordinal)
    {
        if (_generatedStorageReads is null) return false;
        SortAndDedupFlat(_generatedStorageReads, ref _generatedStorageReadsSorted);
        ReadOnlySpan<(int Ordinal, UInt256 Slot)> reads = CollectionsMarshal.AsSpan(_generatedStorageReads);
        return reads.BinarySearch((ordinal, default(UInt256)), OrdinalOnlyComparer.Instance) >= 0;
    }

    /// <summary>BinarySearch by Ordinal only — slot is ignored. The buffer is sorted on
    /// (Ordinal asc, Slot asc), so any entry with the target Ordinal is a match.</summary>
    private sealed class OrdinalOnlyComparer : IComparer<(int Ordinal, UInt256 Slot)>
    {
        public static readonly OrdinalOnlyComparer Instance = new();
        public int Compare((int Ordinal, UInt256 Slot) x, (int Ordinal, UInt256 Slot) y) => x.Ordinal.CompareTo(y.Ordinal);
    }

    /// <summary>
    /// Address of the account assigned this <paramref name="ordinal"/>.
    /// </summary>
    public Address AddressOf(int ordinal) => _addressIndex.GetAddress(ordinal);

    /// <summary>
    /// Iterates ordinals where this index's account-presence bitmap is set, in ascending order.
    /// </summary>
    public IEnumerable<int> EnumerateMarkedOrdinals()
    {
        for (int wordIdx = 0; wordIdx < _hasAccountWords.Length; wordIdx++)
        {
            ulong word = _hasAccountWords[wordIdx];
            while (word != 0)
            {
                int bitOffset = BitOperations.TrailingZeroCount(word);
                yield return (wordIdx << WordShift) + bitOffset;
                word &= word - 1;
            }
        }
    }

    public bool HasAccount(int ordinal) => HasAccountOrdinal(ordinal);

    private static void Count(ReadOnlySpan<ReadOnlyAccountChanges> accounts, LaneSpans counts, uint lastIndex)
    {
        foreach (ReadOnlyAccountChanges accountChanges in accounts)
        {
            Count(accountChanges.BalanceChanges, counts.Balance, lastIndex);
            Count(accountChanges.NonceChanges, counts.Nonce, lastIndex);
            Count(accountChanges.CodeChanges, counts.Code, lastIndex);

            foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
            {
                Count(slotChanges.Changes, counts.Storage, lastIndex);
            }
        }
    }

    private static void Count<TChange>(ReadOnlySpan<TChange> changes, Span<int> counts, uint lastIndex)
        where TChange : struct, IIndexedChange
    {
        for (int i = 0; i < changes.Length; i++)
        {
            if (TryGetRow(changes[i].Index, lastIndex, out int row))
            {
                counts[row]++;
            }
        }
    }

    private bool HasAccountOrdinal(int accountOrdinal) => TestBit(_hasAccountWords, accountOrdinal);

    private static void MarkAccount(ref ulong[] words, int accountOrdinal)
    {
        int word = accountOrdinal >> WordShift;
        if ((uint)word >= (uint)words.Length) PooledArrays.Grow(ref words, word + 1);
        words[word] |= 1UL << (accountOrdinal & BitMask);
    }

    private const int WordShift = 6;
    private const int BitMask = 63;

    private static int WordCount(int bitCount) => (bitCount + BitMask) >> WordShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetBit(ulong[] words, int ordinal) =>
        words[ordinal >> WordShift] |= 1UL << (ordinal & BitMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TestBit(ulong[] words, int ordinal)
    {
        int word = ordinal >> WordShift;
        return (uint)word < (uint)words.Length
            && (words[word] & (1UL << (ordinal & BitMask))) != 0;
    }

    // No callers read `row` on the false return; cheaper to set unconditionally and skip the branch.
    private static bool TryGetRow(uint index, uint lastIndex, out int row)
    {
        row = (int)index;
        return index <= lastIndex;
    }

    private static uint GetLastIndex(int txCount) =>
        checked((uint)txCount + 1);

    internal sealed class AddressIndex
    {
        private readonly Dictionary<AddressAsKey, int> _ordinals = new(AddressAsKey.EqualityComparer);
        // Reverse lookup for slow-path diagnostics that need to translate an ordinal back to an
        // Address (e.g. "incorrect changes for {addr} at index N"). Appended in GetOrAdd.
        private readonly List<Address> _addresses = [];

        public int Count => _ordinals.Count;

        public int GetOrAdd(Address address)
        {
            ref int ordinal = ref CollectionsMarshal.GetValueRefOrAddDefault(_ordinals, address, out bool exists);
            if (!exists)
            {
                ordinal = _ordinals.Count - 1;
                _addresses.Add(address);
            }
            return ordinal;
        }

        public bool TryGet(Address address, out int ordinal) =>
            _ordinals.TryGetValue(address, out ordinal);

        public Address GetAddress(int ordinal) => _addresses[ordinal];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lanes.Dispose();
        if (_hasAccountWords.Length > 0) PooledArrays.Return(_hasAccountWords);
        _hasAccountWords = [];
    }
}
