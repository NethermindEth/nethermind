// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
//     suggested layout; the generator pushes rows into it as the block executes
//     and ChangesEqual(...) compares it against the suggested index after each
//     tx. _hasOutOfRangeChange tracks whether a generated change went past the
//     suggested last index, in which case full-slow validation is required.
internal sealed class BlockAccessListValidationIndex
{
    private readonly AddressIndex _addressIndex;
    private readonly uint _lastIndex;
    private readonly bool _isMutable;
    private readonly Lane<UInt256> _balance;
    private readonly Lane<ulong> _nonce;
    private readonly Lane<ValueHash256> _code;
    private readonly StorageLane _storage;
    private ulong[] _hasAccountWords = [];
    private bool _hasOutOfRangeChange;

    public BlockAccessListValidationIndex(int txCount, AddressIndex addressIndex, BlockAccessListValidationIndex suggested)
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
    }

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

    public static BlockAccessListValidationIndex Build(BlockAccessList blockAccessList, int txCount, AddressIndex addressIndex)
    {
        uint lastIndex = GetLastIndex(txCount);
        int rowCount = checked((int)lastIndex + 1);
        Counts counts = new(rowCount);
        ulong[] hasAccountWords = [];

        foreach (AccountChanges accountChanges in blockAccessList.AccountChangesByAddress)
        {
            int accountOrdinal = addressIndex.GetOrAdd(accountChanges.Address);
            MarkAccount(ref hasAccountWords, accountOrdinal);
        }

        Count(blockAccessList, counts, lastIndex);

        Lane<UInt256> balance = Lane<UInt256>.CreateImmutable(counts.Balance);
        Lane<ulong> nonce = Lane<ulong>.CreateImmutable(counts.Nonce);
        Lane<ValueHash256> code = Lane<ValueHash256>.CreateImmutable(counts.Code);
        StorageLane storage = StorageLane.CreateImmutable(counts.Storage);

        Fill(blockAccessList, addressIndex, lastIndex, balance, nonce, code, storage);

        balance.SortAllRows();
        nonce.SortAllRows();
        code.SortAllRows();
        storage.SortAllRows();

        return new(addressIndex, lastIndex, balance, nonce, code, storage, hasAccountWords);
    }

    public void Add(BlockAccessList blockAccessList)
    {
        if (!_isMutable)
            throw new InvalidOperationException("Only generated validation indexes can be appended.");

        foreach (AccountChanges accountChanges in blockAccessList.UnorderedAccountChanges)
        {
            int accountOrdinal = _addressIndex.GetOrAdd(accountChanges.Address);

            foreach (BalanceChange change in accountChanges.BalanceChangeSet.BlockAccessChanges)
            {
                if (TryGetRow(change.Index, _lastIndex, out int row)) _balance.Add(row, accountOrdinal, change.Value);
                else _hasOutOfRangeChange = true;
            }

            foreach (NonceChange change in accountChanges.NonceChangeSet.BlockAccessChanges)
            {
                if (TryGetRow(change.Index, _lastIndex, out int row)) _nonce.Add(row, accountOrdinal, change.Value);
                else _hasOutOfRangeChange = true;
            }

            foreach (CodeChange change in accountChanges.CodeChangeSet.BlockAccessChanges)
            {
                if (TryGetRow(change.Index, _lastIndex, out int row)) _code.Add(row, accountOrdinal, change.CodeHash);
                else _hasOutOfRangeChange = true;
            }

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                foreach (StorageChange change in slotChanges.Changes.BlockAccessChanges)
                {
                    if (TryGetRow(change.Index, _lastIndex, out int row)) _storage.Add(row, accountOrdinal, slotChanges.Key, change.Value);
                    else _hasOutOfRangeChange = true;
                }
            }
        }

        _balance.SortTouchedRows();
        _nonce.SortTouchedRows();
        _code.SortTouchedRows();
        _storage.SortTouchedRows();
    }

    public bool HasAccount(Address address) =>
        _addressIndex.TryGet(address, out int accountOrdinal) &&
        HasAccountOrdinal(accountOrdinal);

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

    private static void Count(BlockAccessList blockAccessList, Counts counts, uint lastIndex)
    {
        foreach (AccountChanges accountChanges in blockAccessList.UnorderedAccountChanges)
        {
            Count(accountChanges.BalanceChangeSet.BlockAccessChanges, counts.Balance, lastIndex);
            Count(accountChanges.NonceChangeSet.BlockAccessChanges, counts.Nonce, lastIndex);
            Count(accountChanges.CodeChangeSet.BlockAccessChanges, counts.Code, lastIndex);

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                Count(slotChanges.Changes.BlockAccessChanges, counts.Storage, lastIndex);
            }
        }
    }

    private static void Count<TChange>(ReadOnlySpan<TChange> changes, int[] counts, uint lastIndex)
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

    private static void Fill(
        BlockAccessList blockAccessList,
        AddressIndex addressIndex,
        uint lastIndex,
        Lane<UInt256> balance,
        Lane<ulong> nonce,
        Lane<ValueHash256> code,
        StorageLane storage)
    {
        int[] balanceCursors = balance.CreateFillCursors();
        int[] nonceCursors = nonce.CreateFillCursors();
        int[] codeCursors = code.CreateFillCursors();
        int[] storageCursors = storage.CreateFillCursors();

        foreach (AccountChanges accountChanges in blockAccessList.UnorderedAccountChanges)
        {
            bool found = addressIndex.TryGet(accountChanges.Address, out int accountOrdinal);
            Debug.Assert(found);

            foreach (BalanceChange change in accountChanges.BalanceChangeSet.BlockAccessChanges)
            {
                if (TryGetRow(change.Index, lastIndex, out int row)) balance.Fill(row, balanceCursors, accountOrdinal, change.Value);
            }

            foreach (NonceChange change in accountChanges.NonceChangeSet.BlockAccessChanges)
            {
                if (TryGetRow(change.Index, lastIndex, out int row)) nonce.Fill(row, nonceCursors, accountOrdinal, change.Value);
            }

            foreach (CodeChange change in accountChanges.CodeChangeSet.BlockAccessChanges)
            {
                if (TryGetRow(change.Index, lastIndex, out int row)) code.Fill(row, codeCursors, accountOrdinal, change.CodeHash);
            }

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                foreach (StorageChange change in slotChanges.Changes.BlockAccessChanges)
                {
                    if (TryGetRow(change.Index, lastIndex, out int row)) storage.Fill(row, storageCursors, accountOrdinal, slotChanges.Key, change.Value);
                }
            }
        }
    }

    private bool HasAccountOrdinal(int accountOrdinal)
    {
        int word = accountOrdinal >> 6;
        return (uint)word < (uint)_hasAccountWords.Length
            && (_hasAccountWords[word] & (1UL << (accountOrdinal & 63))) != 0;
    }

    private static void MarkAccount(ref ulong[] words, int accountOrdinal)
    {
        int word = accountOrdinal >> 6;
        if ((uint)word >= (uint)words.Length)
            Array.Resize(ref words, Math.Max(word + 1, words.Length == 0 ? 1 : words.Length * 2));
        words[word] |= 1UL << (accountOrdinal & 63);
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

        public int GetOrAdd(Address address)
        {
            ref int ordinal = ref CollectionsMarshal.GetValueRefOrAddDefault(_ordinals, address, out bool exists);
            if (!exists) ordinal = _ordinals.Count - 1;
            return ordinal;
        }

        public bool TryGet(Address address, out int ordinal) =>
            _ordinals.TryGetValue(address, out ordinal);
    }

    private sealed class Counts(int rowCount)
    {
        public readonly int[] Balance = new int[rowCount];
        public readonly int[] Nonce = new int[rowCount];
        public readonly int[] Code = new int[rowCount];
        public readonly int[] Storage = new int[rowCount];
    }

    private sealed class Lane<TValue>
        where TValue : IEquatable<TValue>
    {
        private readonly int[] _rowStarts;
        private readonly int[]? _rowFilled;
        private readonly int[] _accountOrdinals;
        private readonly TValue[] _values;
        private readonly bool[]? _rowTouched;
        private readonly int[]? _touchedRows;
        private readonly bool[]? _rowOverflow;
        private int _touchedCount;

        private Lane(
            int[] rowStarts,
            int[]? rowFilled,
            int[] accountOrdinals,
            TValue[] values,
            bool[]? rowTouched,
            int[]? touchedRows,
            bool[]? rowOverflow)
        {
            _rowStarts = rowStarts;
            _rowFilled = rowFilled;
            _accountOrdinals = accountOrdinals;
            _values = values;
            _rowTouched = rowTouched;
            _touchedRows = touchedRows;
            _rowOverflow = rowOverflow;
        }

        public static Lane<TValue> CreateImmutable(int[] counts)
        {
            int[] rowStarts = CreateRowStarts(counts);
            return new(rowStarts, null, new int[rowStarts[^1]], new TValue[rowStarts[^1]], null, null, null);
        }

        public static Lane<TValue> CreateMutableLike(Lane<TValue> other)
        {
            int rowCount = other.RowCount;
            int[] rowStarts = (int[])other._rowStarts.Clone();
            return new(rowStarts, new int[rowCount], new int[other._accountOrdinals.Length], new TValue[other._values.Length], new bool[rowCount], new int[rowCount], new bool[rowCount]);
        }

        public int RowCount => _rowStarts.Length - 1;

        public int[] CreateFillCursors() =>
            (int[])_rowStarts.Clone();

        public void Fill(int row, int[] cursors, int accountOrdinal, TValue value)
        {
            int offset = cursors[row]++;
            _accountOrdinals[offset] = accountOrdinal;
            _values[offset] = value;
        }

        public void Add(int row, int accountOrdinal, TValue value)
        {
            int[] rowFilled = _rowFilled ?? throw new InvalidOperationException("Cannot append to immutable lane.");
            int filled = rowFilled[row];
            if ((uint)filled >= (uint)Capacity(row))
            {
                _rowOverflow![row] = true;
                return;
            }

            int offset = _rowStarts[row] + filled;
            _accountOrdinals[offset] = accountOrdinal;
            _values[offset] = value;
            rowFilled[row] = filled + 1;
            MarkTouched(row);
        }

        public bool ChangesEqual(Lane<TValue> other, int row)
        {
            if (HasOverflow(row) || other.HasOverflow(row))
            {
                return false;
            }

            int length = RowLength(row);
            if (length != other.RowLength(row))
            {
                return false;
            }

            int start = _rowStarts[row];
            int otherStart = other._rowStarts[row];
            return new ReadOnlySpan<int>(_accountOrdinals, start, length)
                       .SequenceEqual(new ReadOnlySpan<int>(other._accountOrdinals, otherStart, length)) &&
                   new ReadOnlySpan<TValue>(_values, start, length)
                       .SequenceEqual(new ReadOnlySpan<TValue>(other._values, otherStart, length));
        }

        public void SortAllRows()
        {
            for (int row = 0; row < RowCount; row++)
            {
                SortRow(row, Capacity(row));
            }
        }

        public void SortTouchedRows()
        {
            if (_touchedCount == 0)
            {
                return;
            }

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

        private int Capacity(int row) =>
            _rowStarts[row + 1] - _rowStarts[row];

        private int RowLength(int row) =>
            _rowFilled is null ? Capacity(row) : _rowFilled[row];

        private bool HasOverflow(int row) =>
            _rowOverflow is not null && _rowOverflow[row];

        private void MarkTouched(int row)
        {
            bool[] rowTouched = _rowTouched!;
            if (rowTouched[row])
            {
                return;
            }

            rowTouched[row] = true;
            _touchedRows![_touchedCount++] = row;
        }

        private void SortRow(int row, int length)
        {
            if (length <= 1)
            {
                return;
            }

            int start = _rowStarts[row];
            if (length <= 8)
            {
                InsertionSort(start, length);
                return;
            }

            Array.Sort(_accountOrdinals, _values, start, length);
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
    }

    private sealed class StorageLane
    {
        private readonly int[] _rowStarts;
        private readonly int[]? _rowFilled;
        private readonly int[] _accountOrdinals;
        private readonly UInt256[] _keys;
        private readonly EvmWord[] _values;
        private readonly bool[]? _rowTouched;
        private readonly int[]? _touchedRows;
        private readonly bool[]? _rowOverflow;
        private readonly StorageOrderComparer _orderComparer = new();
        private int[] _orderScratch = [];
        private int[] _accountScratch = [];
        private UInt256[] _keyScratch = [];
        private EvmWord[] _valueScratch = [];
        private int _touchedCount;

        private StorageLane(
            int[] rowStarts,
            int[]? rowFilled,
            int[] accountOrdinals,
            UInt256[] keys,
            EvmWord[] values,
            bool[]? rowTouched,
            int[]? touchedRows,
            bool[]? rowOverflow)
        {
            _rowStarts = rowStarts;
            _rowFilled = rowFilled;
            _accountOrdinals = accountOrdinals;
            _keys = keys;
            _values = values;
            _rowTouched = rowTouched;
            _touchedRows = touchedRows;
            _rowOverflow = rowOverflow;
        }

        public static StorageLane CreateImmutable(int[] counts)
        {
            int[] rowStarts = CreateRowStarts(counts);
            return new(rowStarts, null, new int[rowStarts[^1]], new UInt256[rowStarts[^1]], new EvmWord[rowStarts[^1]], null, null, null);
        }

        public static StorageLane CreateMutableLike(StorageLane other)
        {
            int rowCount = other.RowCount;
            int[] rowStarts = (int[])other._rowStarts.Clone();
            return new(rowStarts, new int[rowCount], new int[other._accountOrdinals.Length], new UInt256[other._keys.Length], new EvmWord[other._values.Length], new bool[rowCount], new int[rowCount], new bool[rowCount]);
        }

        public int RowCount => _rowStarts.Length - 1;

        public int[] CreateFillCursors() =>
            (int[])_rowStarts.Clone();

        public void Fill(int row, int[] cursors, int accountOrdinal, UInt256 key, EvmWord value)
        {
            int offset = cursors[row]++;
            _accountOrdinals[offset] = accountOrdinal;
            _keys[offset] = key;
            _values[offset] = value;
        }

        public void Add(int row, int accountOrdinal, UInt256 key, EvmWord value)
        {
            int[] rowFilled = _rowFilled ?? throw new InvalidOperationException("Cannot append to immutable lane.");
            int filled = rowFilled[row];
            if ((uint)filled >= (uint)Capacity(row))
            {
                _rowOverflow![row] = true;
                return;
            }

            int offset = _rowStarts[row] + filled;
            _accountOrdinals[offset] = accountOrdinal;
            _keys[offset] = key;
            _values[offset] = value;
            rowFilled[row] = filled + 1;
            MarkTouched(row);
        }

        public bool ChangesEqual(StorageLane other, int row)
        {
            if (HasOverflow(row) || other.HasOverflow(row))
            {
                return false;
            }

            int length = RowLength(row);
            if (length != other.RowLength(row))
            {
                return false;
            }

            int start = _rowStarts[row];
            int otherStart = other._rowStarts[row];
            return new ReadOnlySpan<int>(_accountOrdinals, start, length)
                       .SequenceEqual(new ReadOnlySpan<int>(other._accountOrdinals, otherStart, length)) &&
                   new ReadOnlySpan<UInt256>(_keys, start, length)
                       .SequenceEqual(new ReadOnlySpan<UInt256>(other._keys, otherStart, length)) &&
                   new ReadOnlySpan<EvmWord>(_values, start, length)
                       .SequenceEqual(new ReadOnlySpan<EvmWord>(other._values, otherStart, length));
        }

        public void SortAllRows()
        {
            for (int row = 0; row < RowCount; row++)
            {
                SortRow(row, Capacity(row));
            }
        }

        public void SortTouchedRows()
        {
            if (_touchedCount == 0)
            {
                return;
            }

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

        private int Capacity(int row) =>
            _rowStarts[row + 1] - _rowStarts[row];

        private int RowLength(int row) =>
            _rowFilled is null ? Capacity(row) : _rowFilled[row];

        private bool HasOverflow(int row) =>
            _rowOverflow is not null && _rowOverflow[row];

        private void MarkTouched(int row)
        {
            bool[] rowTouched = _rowTouched!;
            if (rowTouched[row])
            {
                return;
            }

            rowTouched[row] = true;
            _touchedRows![_touchedCount++] = row;
        }

        // The sort needs to keep three parallel arrays in lock-step, keyed on
        // (accountOrdinal, key) but moving the value alongside. Array.Sort has
        // no overload that sorts three parallel arrays without either boxing
        // (object[] keys) or an indirection array, so we keep a bespoke sort
        // here. Small rows use a stable in-place insertion sort; larger rows
        // sort an index array via Array.Sort + a custom comparer and then
        // gather into scratch buffers (SortWithScratch).
        private void SortRow(int row, int length)
        {
            if (length <= 1)
            {
                return;
            }

            int start = _rowStarts[row];
            if (length <= 8)
            {
                InsertionSort(start, length);
                return;
            }

            SortWithScratch(start, length);
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
            for (int i = 0; i < length; i++)
            {
                _orderScratch[i] = i;
            }

            _orderComparer.Reset(_accountOrdinals, _keys, start);
            Array.Sort(_orderScratch, 0, length, _orderComparer);

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
            if (_orderScratch.Length >= length)
            {
                return;
            }

            _orderScratch = new int[length];
            _accountScratch = new int[length];
            _keyScratch = new UInt256[length];
            _valueScratch = new EvmWord[length];
        }

        private sealed class StorageOrderComparer : IComparer<int>
        {
            private int[] _accountOrdinals = [];
            private UInt256[] _keys = [];
            private int _start;

            public void Reset(int[] accountOrdinals, UInt256[] keys, int start)
            {
                _accountOrdinals = accountOrdinals;
                _keys = keys;
                _start = start;
            }

            public int Compare(int leftOffset, int rightOffset)
            {
                int left = _start + leftOffset;
                int right = _start + rightOffset;
                int accountCompare = _accountOrdinals[left].CompareTo(_accountOrdinals[right]);
                return accountCompare != 0 ? accountCompare : _keys[left].CompareTo(_keys[right]);
            }
        }
    }

    private static int[] CreateRowStarts(int[] counts)
    {
        int[] rowStarts = new int[counts.Length + 1];
        for (int i = 0; i < counts.Length; i++)
        {
            rowStarts[i + 1] = checked(rowStarts[i] + counts[i]);
        }

        return rowStarts;
    }
}
