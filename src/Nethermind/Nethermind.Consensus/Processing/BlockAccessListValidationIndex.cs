// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
internal sealed class BlockAccessListValidationIndex : IDisposable
{
    private readonly AddressIndex _addressIndex;
    private readonly uint _lastIndex;
    private readonly bool _isMutable;
    private readonly Lane<UInt256> _balance;
    private readonly Lane<ulong> _nonce;
    private readonly Lane<ValueHash256> _code;
    private readonly StorageLane _storage;
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
        _balance = Lane<UInt256>.CreateMutableLike(suggested._balance);
        _nonce = Lane<ulong>.CreateMutableLike(suggested._nonce);
        _code = Lane<ValueHash256>.CreateMutableLike(suggested._code);
        _storage = StorageLane.CreateMutableLike(suggested._storage);
        // Slack covers per-tx duplication before dedup on reads, and invalid wire BALs that push
        // generated past suggested before lane overflow trips.
        _generatedStorageReads = new(WithSlack(storageReadsCapacity));
        _generatedStorageWrites = new(WithSlack(storageWritesCapacity));
        _hasAccountWords = SafeArrayPool<ulong>.Shared.Rent(1);
        _hasAccountWords.AsSpan().Clear();
    }

    /// <summary>Capacity hint plus headroom so the lists don't immediately resize on the first
    /// over-count slot.</summary>
    private const int GeneratedListSlackShift = 2;
    private static int WithSlack(int capacity) => capacity + (capacity >> GeneratedListSlackShift);

    private BlockAccessListValidationIndex(
        AddressIndex addressIndex,
        uint lastIndex,
        Lane<UInt256> balance,
        Lane<ulong> nonce,
        Lane<ValueHash256> code,
        StorageLane storage,
        ulong[] hasAccountWords)
    {
        _addressIndex = addressIndex;
        _lastIndex = lastIndex;
        _balance = balance;
        _nonce = nonce;
        _code = code;
        _storage = storage;
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
        Span<int> all = counters.AsSpan();
        CountsView counts = new(
            all.Slice(0, rowCount),
            all.Slice(rowCount, rowCount),
            all.Slice(rowCount * 2, rowCount),
            all.Slice(rowCount * 3, rowCount));

        Count(accounts, counts, lastIndex);

        Lane<UInt256>? balance = null;
        Lane<ulong>? nonce = null;
        Lane<ValueHash256>? code = null;
        StorageLane? storage = null;
        ulong[]? hasAccountWords = null;
        try
        {
            balance = Lane<UInt256>.CreateImmutable(counts.Balance);
            nonce = Lane<ulong>.CreateImmutable(counts.Nonce);
            code = Lane<ValueHash256>.CreateImmutable(counts.Code);
            storage = StorageLane.CreateImmutable(counts.Storage);

            int wordCount = WordCount(accounts.Length);
            hasAccountWords = PooledArrays.RentCleared<ulong>(wordCount);
            FillAndMark(accounts, addressIndex, lastIndex, balance, nonce, code, storage, hasAccountWords);

            balance.SortAllRows();
            nonce.SortAllRows();
            code.SortAllRows();
            storage.SortAllRows();

            return new(addressIndex, lastIndex, balance, nonce, code, storage, hasAccountWords);
        }
        catch
        {
            if (hasAccountWords is not null) PooledArrays.Return(hasAccountWords);
            storage?.Dispose();
            code?.Dispose();
            nonce?.Dispose();
            balance?.Dispose();
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
                if (TryGetRow(balance.Index, _lastIndex, out int row)) RecordIfOverflow(_balance.Add(row, accountOrdinal, balance.Value), balance.Index, accountChanges.Address);
                else _hasOutOfRangeChange = true;
            }

            if (accountChanges.NonceChange is { } nonce)
            {
                if (TryGetRow(nonce.Index, _lastIndex, out int row)) RecordIfOverflow(_nonce.Add(row, accountOrdinal, nonce.Value), nonce.Index, accountChanges.Address);
                else _hasOutOfRangeChange = true;
            }

            if (accountChanges.CodeChange is { } code)
            {
                if (TryGetRow(code.Index, _lastIndex, out int row)) RecordIfOverflow(_code.Add(row, accountOrdinal, code.CodeHash), code.Index, accountChanges.Address);
                else _hasOutOfRangeChange = true;
            }

            List<(int, UInt256)> writes = _generatedStorageWrites!;
            int writesCountBefore = writes.Count;
            foreach (KeyValuePair<UInt256, StorageChange> kv in accountChanges.StorageChanges)
            {
                if (TryGetRow(kv.Value.Index, _lastIndex, out int row)) RecordIfOverflow(_storage.Add(row, accountOrdinal, kv.Key, kv.Value.Value), kv.Value.Index, accountChanges.Address);
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

        _balance.SortTouchedRows();
        _nonce.SortTouchedRows();
        _code.SortTouchedRows();
        _storage.SortTouchedRows();
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

        int row = (int)index;
        return _balance.ChangesEqual(other._balance, row) &&
               _nonce.ChangesEqual(other._nonce, row) &&
               _code.ChangesEqual(other._code, row) &&
               _storage.ChangesEqual(other._storage, row);
    }

    /// <summary>
    /// True iff any of the four lanes has data at (<paramref name="row"/>, <paramref name="ordinal"/>).
    /// </summary>
    public bool HasChangesAtRow(int row, int ordinal) =>
        _balance.HasAt(row, ordinal) || _nonce.HasAt(row, ordinal) ||
        _code.HasAt(row, ordinal) || _storage.HasAt(row, ordinal);

    /// <summary>
    /// True iff both indexes have the same per-lane data at (<paramref name="row"/>, <paramref name="ordinal"/>).
    /// </summary>
    public bool ChangesAtRowEqualForOrdinal(BlockAccessListValidationIndex other, int row, int ordinal)
    {
        // Each scalar lane: presence must match; if both present, value must match.
        if (_balance.HasAt(row, ordinal))
        {
            if (!other._balance.TryGetAt(row, ordinal, out UInt256 otherBal)) return false;
            _balance.TryGetAt(row, ordinal, out UInt256 thisBal);
            if (!thisBal.Equals(otherBal)) return false;
        }
        else if (other._balance.HasAt(row, ordinal)) return false;

        if (_nonce.HasAt(row, ordinal))
        {
            if (!other._nonce.TryGetAt(row, ordinal, out ulong otherN)) return false;
            _nonce.TryGetAt(row, ordinal, out ulong thisN);
            if (thisN != otherN) return false;
        }
        else if (other._nonce.HasAt(row, ordinal)) return false;

        if (_code.HasAt(row, ordinal))
        {
            if (!other._code.TryGetAt(row, ordinal, out ValueHash256 otherC)) return false;
            _code.TryGetAt(row, ordinal, out ValueHash256 thisC);
            if (!thisC.Equals(otherC)) return false;
        }
        else if (other._code.HasAt(row, ordinal)) return false;

        return _storage.SlotsEqualAt(other._storage, row, ordinal);
    }

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

    private static void Count(ReadOnlySpan<ReadOnlyAccountChanges> accounts, CountsView counts, uint lastIndex)
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

    private static void FillAndMark(
        ReadOnlySpan<ReadOnlyAccountChanges> accounts,
        AddressIndex addressIndex,
        uint lastIndex,
        Lane<UInt256> balance,
        Lane<ulong> nonce,
        Lane<ValueHash256> code,
        StorageLane storage,
        ulong[] hasAccountWords)
    {
        // One pooled buffer sliced into four per-lane cursor windows. Each lane's CopyRowStartsTo
        // seeds its slice with the immutable row offsets, then Fill advances the cursor per write.
        int cursorSize = balance.CursorCount;
        using ArrayPoolListRef<int> cursors = new(cursorSize * 4, cursorSize * 4);
        Span<int> allCursors = cursors.AsSpan();
        Span<int> balanceCursorsSpan = allCursors.Slice(0, cursorSize);
        Span<int> nonceCursorsSpan = allCursors.Slice(cursorSize, cursorSize);
        Span<int> codeCursorsSpan = allCursors.Slice(cursorSize * 2, cursorSize);
        Span<int> storageCursorsSpan = allCursors.Slice(cursorSize * 3, cursorSize);
        balance.CopyRowStartsTo(balanceCursorsSpan);
        nonce.CopyRowStartsTo(nonceCursorsSpan);
        code.CopyRowStartsTo(codeCursorsSpan);
        storage.CopyRowStartsTo(storageCursorsSpan);

        foreach (ReadOnlyAccountChanges accountChanges in accounts)
        {
            int accountOrdinal = addressIndex.GetOrAdd(accountChanges.Address);
            SetBit(hasAccountWords, accountOrdinal);

            foreach (BalanceChange change in accountChanges.BalanceChanges)
            {
                if (TryGetRow(change.Index, lastIndex, out int row)) balance.Fill(row, balanceCursorsSpan, accountOrdinal, change.Value);
            }

            foreach (NonceChange change in accountChanges.NonceChanges)
            {
                if (TryGetRow(change.Index, lastIndex, out int row)) nonce.Fill(row, nonceCursorsSpan, accountOrdinal, change.Value);
            }

            foreach (CodeChange change in accountChanges.CodeChanges)
            {
                if (TryGetRow(change.Index, lastIndex, out int row)) code.Fill(row, codeCursorsSpan, accountOrdinal, change.CodeHash);
            }

            foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
            {
                foreach (StorageChange change in slotChanges.Changes)
                {
                    if (TryGetRow(change.Index, lastIndex, out int row)) storage.Fill(row, storageCursorsSpan, accountOrdinal, slotChanges.Key, change.Value);
                }
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

    /// <summary>
    /// Span-based view over four per-row counter buffers. Used by <see cref="Count"/> so it can
    /// fill counters without taking a dependency on whether the buffers came from
    /// <see cref="System.Buffers.ArrayPool{T}"/> or were freshly allocated.
    /// </summary>
    private readonly ref struct CountsView(Span<int> balance, Span<int> nonce, Span<int> code, Span<int> storage)
    {
        public readonly Span<int> Balance = balance;
        public readonly Span<int> Nonce = nonce;
        public readonly Span<int> Code = code;
        public readonly Span<int> Storage = storage;
    }

    /// <summary>
    /// Shared row-indexing, mutable bookkeeping (<c>rowFilled</c> / <c>rowTouched</c> /
    /// <c>rowOverflow</c>), sort scaffolding, and pool lifecycle for <see cref="Lane{TValue}"/>
    /// and <see cref="StorageLane"/>. The derived classes own the per-entry value payload
    /// (one or two parallel arrays) and the row-level <c>SortRow</c> implementation.
    /// </summary>
    private abstract class LaneBase(
        int[] rowStarts,
        int rowStartsLength,
        int[] accountOrdinals,
        int entriesLength,
        MutableBookkeeping? mutable) : IDisposable
    {
        // rowStarts[r] .. rowStarts[r+1] is row r's slice of accountOrdinals + the derived
        // class's value array(s). The mutable lane carries the optional per-row bookkeeping;
        // immutable lanes pass `mutable: null` and the read paths fall back to capacity.
        private int _touchedCount;

        protected int[] RowStarts => rowStarts;
        protected int[] AccountOrdinals => accountOrdinals;
        protected int EntriesLength => entriesLength;

        public int RowCount => rowStartsLength - 1;
        public int CursorCount => rowStartsLength;

        public void CopyRowStartsTo(Span<int> destination)
            => rowStarts.AsSpan(0, rowStartsLength).CopyTo(destination);

        protected int Capacity(int row) => rowStarts[row + 1] - rowStarts[row];

        protected int RowLength(int row) => mutable is { } m ? m.RowFilled[row] : Capacity(row);

        protected bool HasOverflow(int row) => mutable is { } m && m.RowOverflow[row];

        /// <summary>
        /// Reserve the next slot in <paramref name="row"/>. Returns the absolute offset to
        /// write into, or <c>-1</c> if the row is full (in which case the overflow flag is
        /// latched and the caller must report failure).
        /// </summary>
        protected int ReserveNextOffset(int row)
        {
            if (mutable is not { } book) throw new InvalidOperationException("Cannot append to immutable lane.");
            int count = book.RowFilled[row];
            if ((uint)count >= (uint)Capacity(row))
            {
                book.RowOverflow[row] = true;
                return -1;
            }
            int offset = rowStarts[row] + count;
            book.RowFilled[row] = count + 1;
            MarkTouched(book, row);
            return offset;
        }

        public void SortAllRows()
        {
            for (int row = 0; row < RowCount; row++)
                SortRow(row, Capacity(row));
        }

        public void SortTouchedRows()
        {
            if (_touchedCount == 0) return;
            MutableBookkeeping book = mutable!.Value;
            for (int i = 0; i < _touchedCount; i++)
            {
                int row = book.TouchedRows[i];
                SortRow(row, book.RowFilled[row]);
                book.RowTouched[row] = false;
            }
            _touchedCount = 0;
        }

        protected abstract void SortRow(int row, int length);

        private void MarkTouched(MutableBookkeeping book, int row)
        {
            if (book.RowTouched[row]) return;
            book.RowTouched[row] = true;
            book.TouchedRows[_touchedCount++] = row;
        }

        public virtual void Dispose()
        {
            PooledArrays.Return(rowStarts);
            PooledArrays.Return(accountOrdinals);
            mutable?.Return();
        }

        /// <summary>Pool-rents and fills the row-starts prefix-sum array; returns the total
        /// entry count via <paramref name="total"/>.</summary>
        protected static int[] RentRowStarts(ReadOnlySpan<int> counts, out int total)
        {
            int[] rowStarts = PooledArrays.Rent<int>(counts.Length + 1);
            FillRowStarts(counts, rowStarts);
            total = rowStarts[counts.Length];
            return rowStarts;
        }

        /// <summary>Pool-rents a row-starts buffer and copies the first <paramref name="length"/>
        /// entries from <paramref name="source"/> — used when cloning the immutable layout into
        /// a mutable lane.</summary>
        protected static int[] CloneRowStarts(int[] source, int length)
        {
            int[] dest = PooledArrays.Rent<int>(length);
            source.AsSpan(0, length).CopyTo(dest);
            return dest;
        }
    }

    /// <summary>
    /// Small static helper around <see cref="SafeArrayPool{T}"/> that normalises the
    /// <c>Math.Max(size, 1)</c> floor and provides the cleared-rent variant used when a row
    /// of bookkeeping must start at zero.
    /// </summary>
    private static class PooledArrays
    {
        public static T[] Rent<T>(int minLength) => SafeArrayPool<T>.Shared.Rent(Math.Max(minLength, 1));

        public static T[] RentCleared<T>(int length)
        {
            T[] arr = Rent<T>(length);
            arr.AsSpan(0, length).Clear();
            return arr;
        }

        public static void Return<T>(T[] array) => SafeArrayPool<T>.Shared.Return(array);

        /// <summary>
        /// Grow <paramref name="array"/> to fit at least <paramref name="newMinLength"/> entries
        /// while keeping the existing contents. The old buffer is returned to the pool; new
        /// slack is zero-filled so callers can read past the prior length safely.
        /// </summary>
        public static void Grow<T>(ref T[] array, int newMinLength)
        {
            int newSize = Math.Max(newMinLength, array.Length == 0 ? 1 : array.Length * 2);
            T[] newArray = Rent<T>(newSize);
            array.AsSpan().CopyTo(newArray);
            newArray.AsSpan(array.Length).Clear();
            Return(array);
            array = newArray;
        }
    }

    /// <summary>
    /// Four row-aligned bookkeeping buffers a mutable lane needs: <c>RowFilled</c> counts
    /// entries per row, <c>RowTouched</c> + <c>TouchedRows</c> let <c>SortTouchedRows</c> walk
    /// only the changed rows, and <c>RowOverflow</c> latches rows where Add tried to exceed
    /// capacity. All four buffers are pool-rented and returned together.
    /// </summary>
    private readonly struct MutableBookkeeping(int[] rowFilled, bool[] rowTouched, int[] touchedRows, bool[] rowOverflow)
    {
        public readonly int[] RowFilled = rowFilled;
        public readonly bool[] RowTouched = rowTouched;
        public readonly int[] TouchedRows = touchedRows;
        public readonly bool[] RowOverflow = rowOverflow;

        public static MutableBookkeeping ForRowCount(int rowCount) => new(
            PooledArrays.RentCleared<int>(rowCount),
            PooledArrays.RentCleared<bool>(rowCount),
            PooledArrays.Rent<int>(rowCount),
            PooledArrays.RentCleared<bool>(rowCount));

        public void Return()
        {
            PooledArrays.Return(RowFilled);
            PooledArrays.Return(RowTouched);
            PooledArrays.Return(TouchedRows);
            PooledArrays.Return(RowOverflow);
        }
    }

    /// <summary>
    /// On-demand sort scratch buffers for <see cref="StorageLane.SortWithScratch"/>: an
    /// indirection-index array plus three parallel gather buffers. Grows as needed via
    /// <see cref="EnsureLength"/>; all four are released together by <see cref="Return"/>.
    /// </summary>
    private struct StorageScratch
    {
        public int[] Order;
        public int[] Account;
        public UInt256[] Keys;
        public EvmWord[] Values;

        public StorageScratch()
        {
            Order = [];
            Account = [];
            Keys = [];
            Values = [];
        }

        public void EnsureLength(int length)
        {
            if (Order.Length >= length) return;
            Return();
            Order = PooledArrays.Rent<int>(length);
            Account = PooledArrays.Rent<int>(length);
            Keys = PooledArrays.Rent<UInt256>(length);
            Values = PooledArrays.Rent<EvmWord>(length);
        }

        public readonly void Return()
        {
            // Skip empty sentinels — ArrayPool.Return on Array.Empty is harmless but wasteful.
            if (Order.Length > 0) PooledArrays.Return(Order);
            if (Account.Length > 0) PooledArrays.Return(Account);
            if (Keys.Length > 0) PooledArrays.Return(Keys);
            if (Values.Length > 0) PooledArrays.Return(Values);
        }
    }

    private sealed class Lane<TValue>(
        int[] rowStarts, int rowStartsLength,
        int[] accountOrdinals, int entriesLength, TValue[] values,
        MutableBookkeeping? mutable)
        : LaneBase(rowStarts, rowStartsLength, accountOrdinals, entriesLength, mutable)
        where TValue : IEquatable<TValue>
    {
        private readonly TValue[] _values = values;

        public static Lane<TValue> CreateImmutable(ReadOnlySpan<int> counts)
        {
            int[] rowStarts = RentRowStarts(counts, out int total);
            return new(
                rowStarts, counts.Length + 1,
                PooledArrays.Rent<int>(total), total, PooledArrays.Rent<TValue>(total),
                mutable: null);
        }

        public static Lane<TValue> CreateMutableLike(Lane<TValue> other)
        {
            int rowCount = other.RowCount;
            int rowStartsLength = other.CursorCount;
            int entries = other.EntriesLength;
            return new(
                CloneRowStarts(other.RowStarts, rowStartsLength), rowStartsLength,
                PooledArrays.Rent<int>(entries), entries, PooledArrays.Rent<TValue>(entries),
                MutableBookkeeping.ForRowCount(rowCount));
        }

        public void Fill(int row, Span<int> cursors, int accountOrdinal, TValue value)
        {
            int offset = cursors[row]++;
            AccountOrdinals[offset] = accountOrdinal;
            _values[offset] = value;
        }

        public bool Add(int row, int accountOrdinal, TValue value)
        {
            int offset = ReserveNextOffset(row);
            if (offset < 0) return false;
            AccountOrdinals[offset] = accountOrdinal;
            _values[offset] = value;
            return true;
        }

        public bool ChangesEqual(Lane<TValue> other, int row)
        {
            if (HasOverflow(row) || other.HasOverflow(row)) return false;
            int length = RowLength(row);
            if (length != other.RowLength(row)) return false;
            int start = RowStarts[row];
            int otherStart = other.RowStarts[row];
            return new ReadOnlySpan<int>(AccountOrdinals, start, length)
                       .SequenceEqual(new ReadOnlySpan<int>(other.AccountOrdinals, otherStart, length)) &&
                   new ReadOnlySpan<TValue>(_values, start, length)
                       .SequenceEqual(new ReadOnlySpan<TValue>(other._values, otherStart, length));
        }

        /// <summary>
        /// True iff this lane has an entry at (<paramref name="row"/>, <paramref name="ordinal"/>).
        /// Row data is sorted by ordinal so this is a binary search.
        /// </summary>
        public bool HasAt(int row, int ordinal)
        {
            if (HasOverflow(row)) return false;
            int start = RowStarts[row];
            int length = RowLength(row);
            ReadOnlySpan<int> ordinals = new(AccountOrdinals, start, length);
            return ordinals.BinarySearch(ordinal) >= 0;
        }

        public bool TryGetAt(int row, int ordinal, out TValue value)
        {
            if (HasOverflow(row)) { value = default!; return false; }
            int start = RowStarts[row];
            int length = RowLength(row);
            ReadOnlySpan<int> ordinals = new(AccountOrdinals, start, length);
            int idx = ordinals.BinarySearch(ordinal);
            if (idx < 0) { value = default!; return false; }
            value = _values[start + idx];
            return true;
        }

        protected override void SortRow(int row, int length)
        {
            if (length <= 1) return;
            int start = RowStarts[row];
            if (length <= 8) InsertionSort(start, length);
            else Array.Sort(AccountOrdinals, _values, start, length);
        }

        private void InsertionSort(int start, int length)
        {
            for (int i = 1; i < length; i++)
            {
                int accountOrdinal = AccountOrdinals[start + i];
                TValue value = _values[start + i];
                int j = i - 1;
                while (j >= 0 && AccountOrdinals[start + j] > accountOrdinal)
                {
                    AccountOrdinals[start + j + 1] = AccountOrdinals[start + j];
                    _values[start + j + 1] = _values[start + j];
                    j--;
                }
                AccountOrdinals[start + j + 1] = accountOrdinal;
                _values[start + j + 1] = value;
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            PooledArrays.Return(_values);
        }
    }

    private sealed class StorageLane(
        int[] rowStarts, int rowStartsLength,
        int[] accountOrdinals, int entriesLength,
        UInt256[] keys, EvmWord[] values,
        MutableBookkeeping? mutable)
        : LaneBase(rowStarts, rowStartsLength, accountOrdinals, entriesLength, mutable)
    {
        private readonly UInt256[] _keys = keys;
        private readonly EvmWord[] _values = values;
        private StorageScratch _scratch = new();

        public static StorageLane CreateImmutable(ReadOnlySpan<int> counts)
        {
            int[] rowStarts = RentRowStarts(counts, out int total);
            return new(
                rowStarts, counts.Length + 1,
                PooledArrays.Rent<int>(total), total,
                PooledArrays.Rent<UInt256>(total), PooledArrays.Rent<EvmWord>(total),
                mutable: null);
        }

        public static StorageLane CreateMutableLike(StorageLane other)
        {
            int rowCount = other.RowCount;
            int rowStartsLength = other.CursorCount;
            int entries = other.EntriesLength;
            return new(
                CloneRowStarts(other.RowStarts, rowStartsLength), rowStartsLength,
                PooledArrays.Rent<int>(entries), entries,
                PooledArrays.Rent<UInt256>(entries), PooledArrays.Rent<EvmWord>(entries),
                MutableBookkeeping.ForRowCount(rowCount));
        }

        public void Fill(int row, Span<int> cursors, int accountOrdinal, UInt256 key, EvmWord value)
        {
            int offset = cursors[row]++;
            AccountOrdinals[offset] = accountOrdinal;
            _keys[offset] = key;
            _values[offset] = value;
        }

        public bool Add(int row, int accountOrdinal, UInt256 key, EvmWord value)
        {
            int offset = ReserveNextOffset(row);
            if (offset < 0) return false;
            AccountOrdinals[offset] = accountOrdinal;
            _keys[offset] = key;
            _values[offset] = value;
            return true;
        }

        public bool ChangesEqual(StorageLane other, int row)
        {
            if (HasOverflow(row) || other.HasOverflow(row)) return false;
            int length = RowLength(row);
            if (length != other.RowLength(row)) return false;
            int start = RowStarts[row];
            int otherStart = other.RowStarts[row];
            return new ReadOnlySpan<int>(AccountOrdinals, start, length)
                       .SequenceEqual(new ReadOnlySpan<int>(other.AccountOrdinals, otherStart, length)) &&
                   new ReadOnlySpan<UInt256>(_keys, start, length)
                       .SequenceEqual(new ReadOnlySpan<UInt256>(other._keys, otherStart, length)) &&
                   new ReadOnlySpan<EvmWord>(_values, start, length)
                       .SequenceEqual(new ReadOnlySpan<EvmWord>(other._values, otherStart, length));
        }

        /// <summary>
        /// True iff this lane has any (slot, value) entry for <paramref name="ordinal"/> at <paramref name="row"/>.
        /// </summary>
        public bool HasAt(int row, int ordinal)
        {
            if (HasOverflow(row)) return false;
            GetOrdinalRange(row, ordinal, out _, out int length);
            return length > 0;
        }

        /// <summary>
        /// True iff both lanes have identical (slot, value) sequences for <paramref name="ordinal"/> at <paramref name="row"/>.
        /// </summary>
        public bool SlotsEqualAt(StorageLane other, int row, int ordinal)
        {
            if (HasOverflow(row) || other.HasOverflow(row)) return false;
            GetOrdinalRange(row, ordinal, out int thisStart, out int thisLen);
            other.GetOrdinalRange(row, ordinal, out int otherStart, out int otherLen);
            if (thisLen != otherLen) return false;
            return new ReadOnlySpan<UInt256>(_keys, thisStart, thisLen)
                       .SequenceEqual(new ReadOnlySpan<UInt256>(other._keys, otherStart, thisLen)) &&
                   new ReadOnlySpan<EvmWord>(_values, thisStart, thisLen)
                       .SequenceEqual(new ReadOnlySpan<EvmWord>(other._values, otherStart, thisLen));
        }

        // Row data is sorted by (ordinal, key); locates the contiguous run with the given ordinal.
        private void GetOrdinalRange(int row, int ordinal, out int start, out int length)
        {
            int rowStart = RowStarts[row];
            int rowLength = RowLength(row);
            ReadOnlySpan<int> ordinals = new(AccountOrdinals, rowStart, rowLength);
            int lo = LowerBound(ordinals, ordinal);
            int hi = LowerBound(ordinals, ordinal + 1);
            start = rowStart + lo;
            length = hi - lo;
        }

        /// <summary>Smallest index <c>i</c> such that <c>span[i] &gt;= value</c>, or
        /// <c>span.Length</c> if no such index exists. O(log n).</summary>
        private static int LowerBound(ReadOnlySpan<int> span, int value)
        {
            int lo = 0;
            int hi = span.Length;
            while (lo < hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                if (span[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        // Array.Sort has no overload that sorts three parallel arrays without boxing, so we
        // keep a bespoke sort: insertion-sort for small rows, indirection-array + scratch
        // gather for larger ones.
        protected override void SortRow(int row, int length)
        {
            if (length <= 1) return;
            int start = RowStarts[row];
            if (length <= 8) InsertionSort(start, length);
            else SortWithScratch(start, length);
        }

        private void InsertionSort(int start, int length)
        {
            for (int i = 1; i < length; i++)
            {
                int accountOrdinal = AccountOrdinals[start + i];
                UInt256 key = _keys[start + i];
                EvmWord value = _values[start + i];
                int j = i - 1;
                while (j >= 0 && Compare(start + j, accountOrdinal, key) > 0)
                {
                    AccountOrdinals[start + j + 1] = AccountOrdinals[start + j];
                    _keys[start + j + 1] = _keys[start + j];
                    _values[start + j + 1] = _values[start + j];
                    j--;
                }
                AccountOrdinals[start + j + 1] = accountOrdinal;
                _keys[start + j + 1] = key;
                _values[start + j + 1] = value;
            }
        }

        private int Compare(int left, int rightAccountOrdinal, UInt256 rightKey)
        {
            int accountCompare = AccountOrdinals[left].CompareTo(rightAccountOrdinal);
            return accountCompare != 0 ? accountCompare : _keys[left].CompareTo(rightKey);
        }

        private void SortWithScratch(int start, int length)
        {
            _scratch.EnsureLength(length);
            for (int i = 0; i < length; i++) _scratch.Order[i] = i;

            _scratch.Order.AsSpan(0, length).Sort(new StorageOrderComparer(AccountOrdinals, _keys, start));

            for (int i = 0; i < length; i++)
            {
                int source = start + _scratch.Order[i];
                _scratch.Account[i] = AccountOrdinals[source];
                _scratch.Keys[i] = _keys[source];
                _scratch.Values[i] = _values[source];
            }

            _scratch.Account.AsSpan(0, length).CopyTo(AccountOrdinals.AsSpan(start, length));
            _scratch.Keys.AsSpan(0, length).CopyTo(_keys.AsSpan(start, length));
            _scratch.Values.AsSpan(0, length).CopyTo(_values.AsSpan(start, length));
        }

        public override void Dispose()
        {
            base.Dispose();
            PooledArrays.Return(_keys);
            PooledArrays.Return(_values);
            _scratch.Return();
        }

        private readonly struct StorageOrderComparer(int[] accountOrdinals, UInt256[] keys, int start) : IComparer<int>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(int leftOffset, int rightOffset)
            {
                int left = start + leftOffset;
                int right = start + rightOffset;
                int accountCompare = accountOrdinals[left].CompareTo(accountOrdinals[right]);
                return accountCompare != 0 ? accountCompare : keys[left].CompareTo(keys[right]);
            }
        }
    }

    private static void FillRowStarts(ReadOnlySpan<int> counts, int[] rowStarts)
    {
        rowStarts[0] = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            rowStarts[i + 1] = checked(rowStarts[i] + counts[i]);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _balance.Dispose();
        _nonce.Dispose();
        _code.Dispose();
        _storage.Dispose();
        SafeArrayPool<ulong>.Shared.Return(_hasAccountWords);
        _hasAccountWords = [];
    }
}
