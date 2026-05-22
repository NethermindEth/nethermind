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

        using ArrayPoolListRef<int> balanceCounts = new(rowCount, rowCount);
        using ArrayPoolListRef<int> nonceCounts = new(rowCount, rowCount);
        using ArrayPoolListRef<int> codeCounts = new(rowCount, rowCount);
        using ArrayPoolListRef<int> storageCounts = new(rowCount, rowCount);

        Count(accounts, new CountsView(balanceCounts.AsSpan(), nonceCounts.AsSpan(), codeCounts.AsSpan(), storageCounts.AsSpan()), lastIndex);

        Lane<UInt256>? balance = null;
        Lane<ulong>? nonce = null;
        Lane<ValueHash256>? code = null;
        StorageLane? storage = null;
        ulong[]? hasAccountWords = null;
        try
        {
            balance = Lane<UInt256>.CreateImmutable(balanceCounts.AsSpan());
            nonce = Lane<ulong>.CreateImmutable(nonceCounts.AsSpan());
            code = Lane<ValueHash256>.CreateImmutable(codeCounts.AsSpan());
            storage = StorageLane.CreateImmutable(storageCounts.AsSpan());

            int wordCount = WordCount(accounts.Length);
            hasAccountWords = SafeArrayPool<ulong>.Shared.Rent(Math.Max(wordCount, 1));
            hasAccountWords.AsSpan(0, wordCount).Clear();
            FillAndMark(accounts, addressIndex, lastIndex, balance, nonce, code, storage, hasAccountWords);

            balance.SortAllRows();
            nonce.SortAllRows();
            code.SortAllRows();
            storage.SortAllRows();

            return new(addressIndex, lastIndex, balance, nonce, code, storage, hasAccountWords);
        }
        catch
        {
            if (hasAccountWords is not null) SafeArrayPool<ulong>.Shared.Return(hasAccountWords);
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
        int cursorSize = balance.CursorCount;
        using ArrayPoolListRef<int> balanceCursors = new(cursorSize, cursorSize);
        using ArrayPoolListRef<int> nonceCursors = new(cursorSize, cursorSize);
        using ArrayPoolListRef<int> codeCursors = new(cursorSize, cursorSize);
        using ArrayPoolListRef<int> storageCursors = new(cursorSize, cursorSize);

        Span<int> balanceCursorsSpan = balanceCursors.AsSpan();
        Span<int> nonceCursorsSpan = nonceCursors.AsSpan();
        Span<int> codeCursorsSpan = codeCursors.AsSpan();
        Span<int> storageCursorsSpan = storageCursors.AsSpan();
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
        if ((uint)word >= (uint)words.Length)
        {
            ArrayPool<ulong> pool = SafeArrayPool<ulong>.Shared;
            int newSize = Math.Max(word + 1, words.Length == 0 ? 1 : words.Length * 2);
            ulong[] newWords = pool.Rent(newSize);
            words.AsSpan().CopyTo(newWords);
            newWords.AsSpan(words.Length).Clear();
            pool.Return(words);
            words = newWords;
        }
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
        int[]? rowFilled,
        int[] accountOrdinals,
        int entriesLength,
        bool[]? rowTouched,
        int[]? touchedRows,
        bool[]? rowOverflow) : IDisposable
    {
        // _rowStarts[r] .. _rowStarts[r+1] is row r's slice of _accountOrdinals + the derived
        // class's value array(s). Logical lengths (_rowStartsLength / _entriesLength) are
        // tracked separately so oversized pool buffers never leak through reads.
        protected readonly int[] _rowStarts = rowStarts;
        private readonly int _rowStartsLength = rowStartsLength;
        protected readonly int[]? _rowFilled = rowFilled;
        protected readonly int[] _accountOrdinals = accountOrdinals;
        protected readonly int _entriesLength = entriesLength;
        private readonly bool[]? _rowTouched = rowTouched;
        private readonly int[]? _touchedRows = touchedRows;
        private readonly bool[]? _rowOverflow = rowOverflow;
        private int _touchedCount;

        public int RowCount => _rowStartsLength - 1;
        public int CursorCount => _rowStartsLength;

        public void CopyRowStartsTo(Span<int> destination)
            => _rowStarts.AsSpan(0, _rowStartsLength).CopyTo(destination);

        protected int Capacity(int row) => _rowStarts[row + 1] - _rowStarts[row];

        protected int RowLength(int row) => _rowFilled is null ? Capacity(row) : _rowFilled[row];

        protected bool HasOverflow(int row) => _rowOverflow is not null && _rowOverflow[row];

        /// <summary>
        /// Reserve the next slot in <paramref name="row"/>. Returns the absolute offset to
        /// write into, or <c>-1</c> if the row is full (in which case the overflow flag is
        /// latched and the caller must report failure).
        /// </summary>
        protected int ReserveNextOffset(int row)
        {
            int[] rowFilled = _rowFilled ?? throw new InvalidOperationException("Cannot append to immutable lane.");
            int filled = rowFilled[row];
            if ((uint)filled >= (uint)Capacity(row))
            {
                _rowOverflow![row] = true;
                return -1;
            }
            int offset = _rowStarts[row] + filled;
            rowFilled[row] = filled + 1;
            MarkTouched(row);
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
            bool[] rowTouched = _rowTouched!;
            int[] touchedRows = _touchedRows!;
            int[] rowFilled = _rowFilled!;
            for (int i = 0; i < _touchedCount; i++)
            {
                int row = touchedRows[i];
                SortRow(row, rowFilled[row]);
                rowTouched[row] = false;
            }
            _touchedCount = 0;
        }

        protected abstract void SortRow(int row, int length);

        private void MarkTouched(int row)
        {
            bool[] rowTouched = _rowTouched!;
            if (rowTouched[row]) return;
            rowTouched[row] = true;
            _touchedRows![_touchedCount++] = row;
        }

        public virtual void Dispose()
        {
            PooledArrays.Return(_rowStarts);
            PooledArrays.Return(_accountOrdinals);
            if (_rowFilled is not null) PooledArrays.Return(_rowFilled);
            if (_touchedRows is not null) PooledArrays.Return(_touchedRows);
            if (_rowTouched is not null) PooledArrays.Return(_rowTouched);
            if (_rowOverflow is not null) PooledArrays.Return(_rowOverflow);
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
    }

    private sealed class Lane<TValue>(
        int[] rowStarts, int rowStartsLength, int[]? rowFilled,
        int[] accountOrdinals, int entriesLength, TValue[] values,
        bool[]? rowTouched, int[]? touchedRows, bool[]? rowOverflow)
        : LaneBase(rowStarts, rowStartsLength, rowFilled, accountOrdinals, entriesLength,
                   rowTouched, touchedRows, rowOverflow)
        where TValue : IEquatable<TValue>
    {
        private readonly TValue[] _values = values;

        public static Lane<TValue> CreateImmutable(ReadOnlySpan<int> counts)
        {
            int[] rowStarts = RentRowStarts(counts, out int total);
            return new(
                rowStarts, counts.Length + 1, rowFilled: null,
                PooledArrays.Rent<int>(total), total, PooledArrays.Rent<TValue>(total),
                rowTouched: null, touchedRows: null, rowOverflow: null);
        }

        public static Lane<TValue> CreateMutableLike(Lane<TValue> other)
        {
            int rowCount = other.RowCount;
            int rowStartsLength = other.CursorCount;
            int entries = other._entriesLength;
            return new(
                CloneRowStarts(other._rowStarts, rowStartsLength), rowStartsLength,
                PooledArrays.RentCleared<int>(rowCount),
                PooledArrays.Rent<int>(entries), entries, PooledArrays.Rent<TValue>(entries),
                PooledArrays.RentCleared<bool>(rowCount),
                PooledArrays.Rent<int>(rowCount),
                PooledArrays.RentCleared<bool>(rowCount));
        }

        public void Fill(int row, Span<int> cursors, int accountOrdinal, TValue value)
        {
            int offset = cursors[row]++;
            _accountOrdinals[offset] = accountOrdinal;
            _values[offset] = value;
        }

        public bool Add(int row, int accountOrdinal, TValue value)
        {
            int offset = ReserveNextOffset(row);
            if (offset < 0) return false;
            _accountOrdinals[offset] = accountOrdinal;
            _values[offset] = value;
            return true;
        }

        public bool ChangesEqual(Lane<TValue> other, int row)
        {
            if (HasOverflow(row) || other.HasOverflow(row)) return false;
            int length = RowLength(row);
            if (length != other.RowLength(row)) return false;
            int start = _rowStarts[row];
            int otherStart = other._rowStarts[row];
            return new ReadOnlySpan<int>(_accountOrdinals, start, length)
                       .SequenceEqual(new ReadOnlySpan<int>(other._accountOrdinals, otherStart, length)) &&
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
            int start = _rowStarts[row];
            int length = RowLength(row);
            ReadOnlySpan<int> ordinals = new(_accountOrdinals, start, length);
            return ordinals.BinarySearch(ordinal) >= 0;
        }

        public bool TryGetAt(int row, int ordinal, out TValue value)
        {
            if (HasOverflow(row)) { value = default!; return false; }
            int start = _rowStarts[row];
            int length = RowLength(row);
            ReadOnlySpan<int> ordinals = new(_accountOrdinals, start, length);
            int idx = ordinals.BinarySearch(ordinal);
            if (idx < 0) { value = default!; return false; }
            value = _values[start + idx];
            return true;
        }

        protected override void SortRow(int row, int length)
        {
            if (length <= 1) return;
            int start = _rowStarts[row];
            if (length <= 8) InsertionSort(start, length);
            else Array.Sort(_accountOrdinals, _values, start, length);
        }

        private void InsertionSort(int start, int length)
        {
            for (int i = 1; i < length; i++)
            {
                int accountOrdinal = _accountOrdinals[start + i];
                TValue value = _values[start + i];
                int j = i - 1;
                while (j >= 0 && _accountOrdinals[start + j] > accountOrdinal)
                {
                    _accountOrdinals[start + j + 1] = _accountOrdinals[start + j];
                    _values[start + j + 1] = _values[start + j];
                    j--;
                }
                _accountOrdinals[start + j + 1] = accountOrdinal;
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
        int[] rowStarts, int rowStartsLength, int[]? rowFilled,
        int[] accountOrdinals, int entriesLength,
        UInt256[] keys, EvmWord[] values,
        bool[]? rowTouched, int[]? touchedRows, bool[]? rowOverflow)
        : LaneBase(rowStarts, rowStartsLength, rowFilled, accountOrdinals, entriesLength,
                   rowTouched, touchedRows, rowOverflow)
    {
        private readonly UInt256[] _keys = keys;
        private readonly EvmWord[] _values = values;
        // Sort scratch buffers grow on demand via EnsureScratch; empty sentinels for the
        // common "small rows, insertion sort only" case so we don't pay the rent up front.
        private int[] _orderScratch = [];
        private int[] _accountScratch = [];
        private UInt256[] _keyScratch = [];
        private EvmWord[] _valueScratch = [];

        public static StorageLane CreateImmutable(ReadOnlySpan<int> counts)
        {
            int[] rowStarts = RentRowStarts(counts, out int total);
            return new(
                rowStarts, counts.Length + 1, rowFilled: null,
                PooledArrays.Rent<int>(total), total,
                PooledArrays.Rent<UInt256>(total), PooledArrays.Rent<EvmWord>(total),
                rowTouched: null, touchedRows: null, rowOverflow: null);
        }

        public static StorageLane CreateMutableLike(StorageLane other)
        {
            int rowCount = other.RowCount;
            int rowStartsLength = other.CursorCount;
            int entries = other._entriesLength;
            return new(
                CloneRowStarts(other._rowStarts, rowStartsLength), rowStartsLength,
                PooledArrays.RentCleared<int>(rowCount),
                PooledArrays.Rent<int>(entries), entries,
                PooledArrays.Rent<UInt256>(entries), PooledArrays.Rent<EvmWord>(entries),
                PooledArrays.RentCleared<bool>(rowCount),
                PooledArrays.Rent<int>(rowCount),
                PooledArrays.RentCleared<bool>(rowCount));
        }

        public void Fill(int row, Span<int> cursors, int accountOrdinal, UInt256 key, EvmWord value)
        {
            int offset = cursors[row]++;
            _accountOrdinals[offset] = accountOrdinal;
            _keys[offset] = key;
            _values[offset] = value;
        }

        public bool Add(int row, int accountOrdinal, UInt256 key, EvmWord value)
        {
            int offset = ReserveNextOffset(row);
            if (offset < 0) return false;
            _accountOrdinals[offset] = accountOrdinal;
            _keys[offset] = key;
            _values[offset] = value;
            return true;
        }

        public bool ChangesEqual(StorageLane other, int row)
        {
            if (HasOverflow(row) || other.HasOverflow(row)) return false;
            int length = RowLength(row);
            if (length != other.RowLength(row)) return false;
            int start = _rowStarts[row];
            int otherStart = other._rowStarts[row];
            return new ReadOnlySpan<int>(_accountOrdinals, start, length)
                       .SequenceEqual(new ReadOnlySpan<int>(other._accountOrdinals, otherStart, length)) &&
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
            int rowStart = _rowStarts[row];
            int rowLength = RowLength(row);
            ReadOnlySpan<int> ordinals = new(_accountOrdinals, rowStart, rowLength);
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
            int start = _rowStarts[row];
            if (length <= 8) InsertionSort(start, length);
            else SortWithScratch(start, length);
        }

        private void InsertionSort(int start, int length)
        {
            for (int i = 1; i < length; i++)
            {
                int accountOrdinal = _accountOrdinals[start + i];
                UInt256 key = _keys[start + i];
                EvmWord value = _values[start + i];
                int j = i - 1;
                while (j >= 0 && Compare(start + j, accountOrdinal, key) > 0)
                {
                    _accountOrdinals[start + j + 1] = _accountOrdinals[start + j];
                    _keys[start + j + 1] = _keys[start + j];
                    _values[start + j + 1] = _values[start + j];
                    j--;
                }
                _accountOrdinals[start + j + 1] = accountOrdinal;
                _keys[start + j + 1] = key;
                _values[start + j + 1] = value;
            }
        }

        private int Compare(int left, int rightAccountOrdinal, UInt256 rightKey)
        {
            int accountCompare = _accountOrdinals[left].CompareTo(rightAccountOrdinal);
            return accountCompare != 0 ? accountCompare : _keys[left].CompareTo(rightKey);
        }

        private void SortWithScratch(int start, int length)
        {
            EnsureScratch(length);
            for (int i = 0; i < length; i++) _orderScratch[i] = i;

            _orderScratch.AsSpan(0, length).Sort(new StorageOrderComparer(_accountOrdinals, _keys, start));

            for (int i = 0; i < length; i++)
            {
                int source = start + _orderScratch[i];
                _accountScratch[i] = _accountOrdinals[source];
                _keyScratch[i] = _keys[source];
                _valueScratch[i] = _values[source];
            }

            _accountScratch.AsSpan(0, length).CopyTo(_accountOrdinals.AsSpan(start, length));
            _keyScratch.AsSpan(0, length).CopyTo(_keys.AsSpan(start, length));
            _valueScratch.AsSpan(0, length).CopyTo(_values.AsSpan(start, length));
        }

        private void EnsureScratch(int length)
        {
            if (_orderScratch.Length >= length) return;
            ReturnScratchIfRented();
            _orderScratch = PooledArrays.Rent<int>(length);
            _accountScratch = PooledArrays.Rent<int>(length);
            _keyScratch = PooledArrays.Rent<UInt256>(length);
            _valueScratch = PooledArrays.Rent<EvmWord>(length);
        }

        private void ReturnScratchIfRented()
        {
            // Skip empty sentinels — ArrayPool.Return on Array.Empty is harmless but wasteful.
            if (_orderScratch.Length > 0) PooledArrays.Return(_orderScratch);
            if (_accountScratch.Length > 0) PooledArrays.Return(_accountScratch);
            if (_keyScratch.Length > 0) PooledArrays.Return(_keyScratch);
            if (_valueScratch.Length > 0) PooledArrays.Return(_valueScratch);
        }

        public override void Dispose()
        {
            base.Dispose();
            PooledArrays.Return(_keys);
            PooledArrays.Return(_values);
            ReturnScratchIfRented();
        }

        private readonly struct StorageOrderComparer(int[] accountOrdinals, UInt256[] keys, int start) : IComparer<int>
        {
            private readonly int[] _accountOrdinals = accountOrdinals;
            private readonly UInt256[] _keys = keys;
            private readonly int _start = start;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(int leftOffset, int rightOffset)
            {
                int left = _start + leftOffset;
                int right = _start + rightOffset;
                int accountCompare = _accountOrdinals[left].CompareTo(_accountOrdinals[right]);
                return accountCompare != 0 ? accountCompare : _keys[left].CompareTo(_keys[right]);
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
