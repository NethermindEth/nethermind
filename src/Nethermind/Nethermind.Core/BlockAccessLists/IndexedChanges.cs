// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Stores EIP-7928 changes by block access index with the internal prestate sentinel
/// kept outside the append-only block-access-index lane.
/// </summary>
[JsonConverter(typeof(IndexedChangesJsonConverterFactory))]
public sealed class IndexedChanges<T> where T : struct, IIndexedChange
{
    private bool _hasPrestate;
    private T _prestate;
    private readonly List<T> _changes;

    public IndexedChanges() => _changes = [];

    public IndexedChanges(int capacity) => _changes = capacity == 0 ? [] : new(capacity);

    public int Count => _changes.Count + (_hasPrestate ? 1 : 0);

    public bool HasChanges => _changes.Count != 0;

    internal bool HasPrestate => _hasPrestate;

    internal ref readonly T Prestate => ref _prestate;

    public IndexedChangeKeys<T> Keys => new(this);

    public IndexedChangeValues<T> Values => new(this);

    public T this[uint index]
    {
        get
        {
            if (TryGetValue(index, out T change))
            {
                return change;
            }

            return ThrowKeyNotFound(index);
        }
        set
        {
            if (value.Index != index)
            {
                ThrowIndexMismatch(index, value.Index, nameof(value));
            }

            Set(value);
        }
    }

    public static IndexedChanges<T> FromSortedList(SortedList<uint, T> changes)
    {
        IndexedChanges<T> indexed = new(changes.Count);
        IList<T> values = changes.Values;
        for (int i = 0; i < values.Count; i++)
        {
            indexed.Add(values[i]);
        }

        return indexed;
    }

    public void Add(uint index, T change)
    {
        if (change.Index != index)
        {
            ThrowIndexMismatch(index, change.Index, nameof(change));
        }

        Add(change);
    }

    public void Add(T change)
    {
        uint index = change.Index;
        if (index == Eip7928Constants.PrestateIndex)
        {
            if (_hasPrestate)
            {
                ThrowDuplicateIndex(index);
            }

            _prestate = change;
            _hasPrestate = true;
            return;
        }

        if (_changes.Count != 0)
        {
            uint lastIndex = _changes[^1].Index;
            if (index <= lastIndex)
            {
                if (index == lastIndex)
                {
                    ThrowDuplicateIndex(index);
                }

                ThrowNonMonotonicIndex(index, lastIndex);
            }
        }

        _changes.Add(change);
    }

    public void Set(T change)
    {
        uint index = change.Index;
        if (index == Eip7928Constants.PrestateIndex)
        {
            _prestate = change;
            _hasPrestate = true;
            return;
        }

        int count = _changes.Count;
        if (count == 0)
        {
            _changes.Add(change);
            return;
        }

        uint lastIndex = _changes[count - 1].Index;
        if (index > lastIndex)
        {
            _changes.Add(change);
            return;
        }

        if (index == lastIndex)
        {
            _changes[count - 1] = change;
            return;
        }

        int existingIndex = FindIndex(index);
        if (existingIndex >= 0)
        {
            _changes[existingIndex] = change;
            return;
        }

        ThrowNonMonotonicIndex(index, lastIndex);
    }

    public void AddRange(IndexedChanges<T> other)
    {
        if (other._hasPrestate)
        {
            Add(other._prestate);
        }

        ReadOnlySpan<T> otherChanges = other.BlockAccessChanges;
        _changes.EnsureCapacity(_changes.Count + otherChanges.Length);
        for (int i = 0; i < otherChanges.Length; i++)
        {
            Add(otherChanges[i]);
        }
    }

    public void SetRange(IndexedChanges<T> other)
    {
        if (other._hasPrestate)
        {
            Set(other._prestate);
        }

        ReadOnlySpan<T> otherChanges = other.BlockAccessChanges;
        _changes.EnsureCapacity(_changes.Count + otherChanges.Length);
        for (int i = 0; i < otherChanges.Length; i++)
        {
            Set(otherChanges[i]);
        }
    }

    public bool ContainsKey(uint index) => TryGetValue(index, out _);

    public bool TryGetValue(uint index, out T change)
    {
        if (index == Eip7928Constants.PrestateIndex)
        {
            change = _prestate;
            return _hasPrestate;
        }

        int changeIndex = FindIndex(index);
        if (changeIndex >= 0)
        {
            change = _changes[changeIndex];
            return true;
        }

        change = default;
        return false;
    }

    public bool TryPopLast(uint index, out T change)
    {
        int count = _changes.Count;
        if (count != 0)
        {
            T last = _changes[count - 1];
            if (last.Index == index)
            {
                _changes.RemoveAt(count - 1);
                change = last;
                return true;
            }
        }
        else if (_hasPrestate && index == Eip7928Constants.PrestateIndex)
        {
            change = _prestate;
            _prestate = default;
            _hasPrestate = false;
            return true;
        }

        change = default;
        return false;
    }

    public void RemoveAt(int index)
    {
        int changeIndex = ToChangeOrdinal(index);
        if (changeIndex < 0)
        {
            _prestate = default;
            _hasPrestate = false;
            return;
        }

        _changes.RemoveAt(changeIndex);
    }

    public void Clear()
    {
        _prestate = default;
        _hasPrestate = false;
        _changes.Clear();
    }

    public bool TryGetLast([NotNullWhen(true)] out T change)
    {
        int count = _changes.Count;
        if (count != 0)
        {
            change = _changes[count - 1];
            return true;
        }

        if (_hasPrestate)
        {
            change = _prestate;
            return true;
        }

        change = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetLastBeforeOrPrestate(uint blockAccessIndex, out T change)
    {
        if (TryGetLastBefore(blockAccessIndex, out change))
        {
            return true;
        }

        if (_hasPrestate)
        {
            change = _prestate;
            return true;
        }

        change = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetLastBefore(uint blockAccessIndex, out T change)
    {
        ReadOnlySpan<T> changes = BlockAccessChanges;
        int length = changes.Length;
        if (length != 0)
        {
            // Fast path: target is past every recorded change (post-block reads, and any
            // read where the slot was last touched before the current block-access index).
            ref readonly T tail = ref changes[length - 1];
            if (tail.Index < blockAccessIndex)
            {
                change = tail;
                return true;
            }

            // Fall through to a binary search only when the interior could contain the
            // boundary; if the first element is already at-or-after the target, no real
            // change qualifies as "before".
            if (changes[0].Index < blockAccessIndex)
            {
                change = changes[FindFirstIndexAtOrAfter(blockAccessIndex) - 1];
                return true;
            }
        }

        change = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasBefore(uint blockAccessIndex)
    {
        // Monotonic Index ordering: at least one element is before the target iff the
        // first element is. Avoids the O(log n) binary search the previous form did.
        ReadOnlySpan<T> changes = BlockAccessChanges;
        return changes.Length != 0 && changes[0].Index < blockAccessIndex;
    }

    public Enumerator GetEnumerator() => new(this);

    internal ReadOnlySpan<T> BlockAccessChanges => CollectionsMarshal.AsSpan(_changes);

    internal T GetAt(int index)
    {
        int changeIndex = ToChangeOrdinal(index);
        return changeIndex < 0 ? _prestate : _changes[changeIndex];
    }

    internal uint GetKeyAt(int index) => GetAt(index).Index;

    private int ToChangeOrdinal(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            ThrowIndexOutOfRange();
        }

        if (_hasPrestate)
        {
            if (index == 0)
            {
                return -1;
            }

            index--;
        }

        return index;
    }

    private int FindIndex(uint index)
    {
        ReadOnlySpan<T> changes = BlockAccessChanges;
        int low = 0;
        int high = changes.Length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            uint midIndex = changes[mid].Index;
            if (midIndex == index)
            {
                return mid;
            }

            if (midIndex < index)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }

    private int FindFirstIndexAtOrAfter(uint blockAccessIndex)
    {
        ReadOnlySpan<T> changes = BlockAccessChanges;
        int low = 0;
        int high = changes.Length;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (changes[mid].Index < blockAccessIndex)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    [DoesNotReturn, StackTraceHidden]
    private static T ThrowKeyNotFound(uint index) =>
        throw new KeyNotFoundException($"No {typeof(T).Name} at block access index {index}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIndexMismatch(uint keyIndex, uint changeIndex, string paramName) =>
        throw new ArgumentException($"{typeof(T).Name} index {changeIndex} does not match key {keyIndex}.", paramName);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowDuplicateIndex(uint index) =>
        throw new ArgumentException($"{typeof(T).Name} already contains block access index {index}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNonMonotonicIndex(uint index, uint lastIndex) =>
        throw new InvalidOperationException($"{typeof(T).Name} index {index} cannot be appended after {lastIndex}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index");

    public struct Enumerator
    {
        private readonly IndexedChanges<T> _changes;
        private int _index;

        internal Enumerator(IndexedChanges<T> changes)
        {
            _changes = changes;
            _index = -1;
        }

        public KeyValuePair<uint, T> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                T change = _changes.GetAt(_index);
                return new(change.Index, change);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => ++_index < _changes.Count;
    }
}

public readonly struct IndexedChangeValues<T> : IReadOnlyList<T> where T : struct, IIndexedChange
{
    private readonly IndexedChanges<T> _changes;

    internal IndexedChangeValues(IndexedChanges<T> changes) => _changes = changes;

    public int Count => _changes.Count;

    public T this[int index] => _changes.GetAt(index);

    public Enumerator GetEnumerator() => new(_changes);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly IndexedChanges<T> _changes;
        private int _index;

        internal Enumerator(IndexedChanges<T> changes)
        {
            _changes = changes;
            _index = -1;
        }

        public T Current => _changes.GetAt(_index);

        object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _changes.Count;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}

public readonly struct IndexedChangeKeys<T> : IReadOnlyList<uint> where T : struct, IIndexedChange
{
    private readonly IndexedChanges<T> _changes;

    internal IndexedChangeKeys(IndexedChanges<T> changes) => _changes = changes;

    public int Count => _changes.Count;

    public uint this[int index] => _changes.GetKeyAt(index);

    public Enumerator GetEnumerator() => new(_changes);

    IEnumerator<uint> IEnumerable<uint>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<uint>
    {
        private readonly IndexedChanges<T> _changes;
        private int _index;

        internal Enumerator(IndexedChanges<T> changes)
        {
            _changes = changes;
            _index = -1;
        }

        public uint Current => _changes.GetKeyAt(_index);

        object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _changes.Count;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}
