// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

// LaneStore + Lane<TValue> + StorageLane + LaneBase form the row-aligned, pool-backed
// storage tier of the validation index. Split into its own file so the main
// BlockAccessListValidationIndex.cs can stay focused on the index's public surface
// (Build / Add / ChangesEqual / FindStructuralMismatch / etc.).
internal sealed partial class BlockAccessListValidationIndex
{
    /// <summary>
    /// Four per-lane <c>Span&lt;int&gt;</c> views — same shape for the row-counters in
    /// <see cref="Build"/> and the row-fill cursors in <see cref="LaneStore.FillFromAccounts"/>.
    /// <see cref="PartitionInQuarters"/> carves a single pooled buffer into one of these.
    /// </summary>
    internal readonly ref struct LaneSpans(Span<int> balance, Span<int> nonce, Span<int> code, Span<int> storage)
    {
        public readonly Span<int> Balance = balance;
        public readonly Span<int> Nonce = nonce;
        public readonly Span<int> Code = code;
        public readonly Span<int> Storage = storage;
    }

    /// <summary>
    /// Carve <paramref name="source"/> into four equal <paramref name="chunkSize"/>-sized
    /// per-lane spans.
    /// </summary>
    private static LaneSpans PartitionInQuarters(Span<int> source, int chunkSize) => new(
        source.Slice(0, chunkSize),
        source.Slice(chunkSize, chunkSize),
        source.Slice(chunkSize * 2, chunkSize),
        source.Slice(chunkSize * 3, chunkSize));

    /// <summary>
    /// The four lanes (balance / nonce / code / storage) bundled with all per-row operations
    /// the validation index needs. The lane references are private — callers go through the
    /// methods below, so the heterogeneity of <see cref="Lane{TValue}"/> / <see cref="StorageLane"/>
    /// is hidden inside the struct.
    /// </summary>
    internal readonly struct LaneStore
    {
        private readonly Lane<UInt256> _balance;
        private readonly Lane<ulong> _nonce;
        private readonly Lane<ValueHash256> _code;
        private readonly StorageLane _storage;

        /// <summary>Build the immutable lane storage from per-row counters.</summary>
        public LaneStore(LaneSpans counts)
        {
            try
            {
                _balance = Lane<UInt256>.CreateImmutable(counts.Balance);
                _nonce = Lane<ulong>.CreateImmutable(counts.Nonce);
                _code = Lane<ValueHash256>.CreateImmutable(counts.Code);
                _storage = StorageLane.CreateImmutable(counts.Storage);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Build mutable lane storage that mirrors <paramref name="other"/>'s layout.</summary>
        public LaneStore(LaneStore other)
        {
            try
            {
                _balance = Lane<UInt256>.CreateMutableLike(other._balance);
                _nonce = Lane<ulong>.CreateMutableLike(other._nonce);
                _code = Lane<ValueHash256>.CreateMutableLike(other._code);
                _storage = StorageLane.CreateMutableLike(other._storage);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void SortAllRows()
        {
            _balance.SortAllRows();
            _nonce.SortAllRows();
            _code.SortAllRows();
            _storage.SortAllRows();
        }

        public void SortTouchedRows()
        {
            _balance.SortTouchedRows();
            _nonce.SortTouchedRows();
            _code.SortTouchedRows();
            _storage.SortTouchedRows();
        }

        public bool ChangesEqual(LaneStore other, int row) =>
            _balance.ChangesEqual(other._balance, row) &&
            _nonce.ChangesEqual(other._nonce, row) &&
            _code.ChangesEqual(other._code, row) &&
            _storage.ChangesEqual(other._storage, row);

        public bool HasAt(int row, int ordinal) =>
            _balance.HasAt(row, ordinal) || _nonce.HasAt(row, ordinal) ||
            _code.HasAt(row, ordinal) || _storage.HasAt(row, ordinal);

        public bool ChangesAtRowEqualForOrdinal(LaneStore other, int row, int ordinal) =>
            ScalarLaneEqualAt(_balance, other._balance, row, ordinal) &&
            ScalarLaneEqualAt(_nonce, other._nonce, row, ordinal) &&
            ScalarLaneEqualAt(_code, other._code, row, ordinal) &&
            _storage.SlotsEqualAt(other._storage, row, ordinal);

        public bool TryAddBalance(int row, int ordinal, in UInt256 value) => _balance.Add(row, ordinal, value);
        public bool TryAddNonce(int row, int ordinal, ulong value) => _nonce.Add(row, ordinal, value);
        public bool TryAddCode(int row, int ordinal, in ValueHash256 hash) => _code.Add(row, ordinal, hash);
        public bool TryAddStorage(int row, int ordinal, in UInt256 key, in EvmWord value) => _storage.Add(row, ordinal, key, value);

        /// <summary>
        /// Walk every account in <paramref name="accounts"/>, assign it an ordinal via
        /// <paramref name="addressIndex"/>, mark it in <paramref name="hasAccountWords"/>, and
        /// fill each lane with that account's per-tx changes. Drives the immutable index's
        /// initial population.
        /// </summary>
        public void FillFromAccounts(
            ReadOnlySpan<ReadOnlyAccountChanges> accounts,
            AddressIndex addressIndex,
            uint lastIndex,
            ulong[] hasAccountWords)
        {
            // One pooled buffer sliced into four per-lane cursor windows. CopyAllRowStartsTo
            // seeds each slice with the immutable row offsets; Fill advances the cursor per write.
            int cursorSize = _balance.CursorCount;
            using ArrayPoolListRef<int> cursors = new(cursorSize * 4, cursorSize * 4);
            LaneSpans c = PartitionInQuarters(cursors.AsSpan(), cursorSize);
            _balance.CopyRowStartsTo(c.Balance);
            _nonce.CopyRowStartsTo(c.Nonce);
            _code.CopyRowStartsTo(c.Code);
            _storage.CopyRowStartsTo(c.Storage);

            foreach (ReadOnlyAccountChanges account in accounts)
            {
                int accountOrdinal = addressIndex.GetOrAdd(account.Address);
                SetBit(hasAccountWords, accountOrdinal);

                _balance.FillFromChanges<BalanceChange>(account.BalanceChanges, c.Balance, accountOrdinal, lastIndex, static b => b.Value);
                _nonce.FillFromChanges<NonceChange>(account.NonceChanges, c.Nonce, accountOrdinal, lastIndex, static n => n.Value);
                _code.FillFromChanges<CodeChange>(account.CodeChanges, c.Code, accountOrdinal, lastIndex, static cc => cc.CodeHash);
                _storage.FillFromStorageChanges(account.StorageChanges, c.Storage, accountOrdinal, lastIndex);
            }
        }

        public void Dispose()
        {
            _balance?.Dispose();
            _nonce?.Dispose();
            _code?.Dispose();
            _storage?.Dispose();
        }

        private static bool ScalarLaneEqualAt<TValue>(Lane<TValue> a, Lane<TValue> b, int row, int ordinal)
            where TValue : IEquatable<TValue>
        {
            bool aHas = a.TryGetAt(row, ordinal, out TValue aVal);
            bool bHas = b.TryGetAt(row, ordinal, out TValue bVal);
            // Equal iff both lanes agree on presence and, when present, on the value.
            return aHas == bHas && (!aHas || aVal.Equals(bVal));
        }
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
            // _touchedCount > 0 implies mutable is set: MarkTouched only increments via
            // ReserveNextOffset, which throws when mutable is null.
            Debug.Assert(mutable.HasValue, "SortTouchedRows ran with _touchedCount > 0 but no mutable bookkeeping.");
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

        /// <summary>
        /// Pool-rents and fills the row-starts prefix-sum array; returns the total
        /// entry count via <paramref name="total"/>.
        /// </summary>
        protected static int[] RentRowStarts(ReadOnlySpan<int> counts, out int total)
        {
            int[] rowStarts = PooledArrays.Rent<int>(counts.Length + 1);
            FillRowStarts(counts, rowStarts);
            total = rowStarts[counts.Length];
            return rowStarts;
        }

        /// <summary>
        /// Pool-rents a row-starts buffer and copies the first <paramref name="length"/>
        /// entries from <paramref name="source"/> — used when cloning the immutable layout into
        /// a mutable lane.
        /// </summary>
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
        /// while keeping the existing contents. A non-empty old buffer is returned to the pool;
        /// new slack is zero-filled so callers can read past the prior length safely. Safe to
        /// call when <paramref name="array"/> is empty (sentinel start state).
        /// </summary>
        public static void Grow<T>(ref T[] array, int newMinLength)
        {
            int newSize = Math.Max(newMinLength, array.Length == 0 ? 1 : array.Length * 2);
            T[] newArray = Rent<T>(newSize);
            array.AsSpan().CopyTo(newArray);
            newArray.AsSpan(array.Length).Clear();
            if (array.Length > 0) Return(array);
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

        /// <summary>
        /// Fill the lane with one account's <typeparamref name="TChange"/> entries. Each change's
        /// (Index, value) is routed into the appropriate row via the supplied cursors. Indices
        /// past <paramref name="lastIndex"/> are skipped — the caller already validated them.
        /// </summary>
        public void FillFromChanges<TChange>(
            ReadOnlySpan<TChange> changes, Span<int> cursors, int accountOrdinal, uint lastIndex,
            Func<TChange, TValue> select)
            where TChange : struct, IIndexedChange
        {
            foreach (TChange change in changes)
                if (TryGetRow(change.Index, lastIndex, out int row)) Fill(row, cursors, accountOrdinal, select(change));
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
            return AccountOrdinals.SliceEqual(start, other.AccountOrdinals, otherStart, length) &&
                   _values.SliceEqual(start, other._values, otherStart, length);
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
            AccountOrdinals.AsSpan(start, length).Sort(_values.AsSpan(start, length));
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

        /// <summary>
        /// Fill the lane with one account's storage_changes. Walks each slot's per-tx
        /// <see cref="StorageChange"/> sequence; (Index, slotKey, value) for each in-range
        /// change is routed into the appropriate row via the supplied cursors.
        /// </summary>
        public void FillFromStorageChanges(
            ReadOnlySpan<ReadOnlySlotChanges> storageChanges, Span<int> cursors, int accountOrdinal, uint lastIndex)
        {
            foreach (ReadOnlySlotChanges slotChanges in storageChanges)
                foreach (StorageChange change in slotChanges.Changes)
                    if (TryGetRow(change.Index, lastIndex, out int row)) Fill(row, cursors, accountOrdinal, slotChanges.Key, change.Value);
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
            return AccountOrdinals.SliceEqual(start, other.AccountOrdinals, otherStart, length) &&
                   _keys.SliceEqual(start, other._keys, otherStart, length) &&
                   _values.SliceEqual(start, other._values, otherStart, length);
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
            return _keys.SliceEqual(thisStart, other._keys, otherStart, thisLen) &&
                   _values.SliceEqual(thisStart, other._values, otherStart, thisLen);
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

        // Three parallel arrays (ordinals, keys, values) — no BCL overload sorts three at once,
        // so we sort an indirection array via Span.Sort and gather through scratch buffers.
        protected override void SortRow(int row, int length)
        {
            if (length <= 1) return;
            int start = RowStarts[row];
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

            _scratch.Account.CopySlice(0, AccountOrdinals, start, length);
            _scratch.Keys.CopySlice(0, _keys, start, length);
            _scratch.Values.CopySlice(0, _values, start, length);
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
}
