// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Collections;

public sealed class ArrayPoolList<T> : IList<T>, IList, IOwnedReadOnlyList<T>
{
    private readonly ArrayPool<T> _arrayPool;
    private T[] _array;
    private int _capacity;
    private bool _disposed;

    public ArrayPoolList(int capacity) : this(ArrayPool<T>.Shared, capacity) { }

    public ArrayPoolList(int capacity, int count) : this(ArrayPool<T>.Shared, capacity, count) { }

    public ArrayPoolList(int capacity, IEnumerable<T> enumerable) : this(capacity) => this.AddRange(enumerable);

    public ArrayPoolList(ReadOnlySpan<T> span) : this(span.Length) => AddRange(span);

    public ArrayPoolList(ArrayPool<T> arrayPool, int capacity, int startingCount = 0)
    {
        _arrayPool = arrayPool;

        if (capacity != 0)
        {
            _array = arrayPool.Rent(capacity);
            _array.AsSpan(0, startingCount).Clear();
        }
        else
        {
            _array = [];
        }
        _capacity = _array.Length;

        Count = startingCount;
    }

    ReadOnlySpan<T> IOwnedReadOnlyList<T>.AsSpan()
    {
        return AsSpan();
    }

    public PooledArrayEnumerator<T> GetEnumerator()
    {
        GuardDispose();
        return new PooledArrayEnumerator<T>(_array, Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GuardDispose()
    {
        if (_disposed)
        {
            ThrowObjectDisposed();
        }

        [DoesNotReturn]
        static void ThrowObjectDisposed()
        {
            throw new ObjectDisposedException(nameof(ArrayPoolList<T>));
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(T item)
    {
        GuardResize();
        _array[Count++] = item;
    }

    int IList.Add(object? value)
    {
        ThrowHelper.IfNullAndNullsAreIllegalThenThrow<T>(value, nameof(value));

        Add((T)value!);

        return Count - 1;
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        GuardResize(items.Length);
        items.CopyTo(_array.AsSpan(Count, items.Length));
        Count += items.Length;
    }

    public void Clear()
    {
        ClearToCount(_array);
        Count = 0;
    }

    public bool Contains(T item)
    {
        GuardDispose();
        int indexOf = Array.IndexOf(_array, item);
        return indexOf >= 0 && indexOf < Count;
    }

    bool IList.Contains(object? value) => IsCompatibleObject(value) && Contains((T)value!);

    public void CopyTo(T[] array, int arrayIndex)
    {
        GuardDispose();
        _array.AsMemory(0, Count).CopyTo(array.AsMemory(arrayIndex));
    }

    void ICollection.CopyTo(Array array, int index)
    {
        if ((array is not null) && (array.Rank != 1))
            throw new ArgumentException("Only single dimensional arrays are supported.", nameof(array));

        GuardDispose();

        Array.Copy(_array, 0, array!, index, Count);
    }

    public int Count { get; private set; } = 0;
    public void ReduceCount(int count)
    {
        GuardDispose();
        var oldCount = Count;
        if (count == oldCount) return;

        if (count > oldCount)
        {
            ThrowOnlyReduce(count);
        }

        Count = count;
        if (count < _capacity / 2)
        {
            // Reduced to less than half of the capacity, resize the array.
            T[] newArray = _arrayPool.Rent(count);
            _array.AsSpan(0, count).CopyTo(newArray);
            T[] oldArray = Interlocked.Exchange(ref _array, newArray);
            _capacity = newArray.Length;
            ClearToCount(oldArray);
            _arrayPool.Return(oldArray);
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            // Release any references to the objects in the array that are no longer in use.
            Array.Clear(_array, count, oldCount - count);
        }

        void ThrowOnlyReduce(int count)
        {
            throw new ArgumentException($"Count can only be reduced. {count} is larger than {Count}", nameof(count));
        }
    }

    private void ClearToCount(T[] array)
    {
        int count = Count;
        // Release any references to the objects in the array so can be GC'd.
        if (count > 0 && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array, 0, count);
        }
    }

    public void Sort(Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        GuardDispose();

        if (Count > 1)
        {
            _array.AsSpan(0, Count).Sort(comparison);
        }
    }

    public int Capacity => _capacity;

    bool IList.IsFixedSize => false;

    bool ICollection<T>.IsReadOnly => false;

    bool IList.IsReadOnly => false;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public int IndexOf(T item)
    {
        GuardDispose();
        int indexOf = Array.IndexOf(_array, item);
        return indexOf < Count ? indexOf : -1;
    }

    int IList.IndexOf(object? value) => IsCompatibleObject(value) ? IndexOf((T)value!) : -1;

    public void Insert(int index, T item)
    {
        GuardResize();
        GuardIndex(index, allowEqualToCount: true);
        _array.AsMemory(index, Count - index).CopyTo(_array.AsMemory(index + 1));
        _array[index] = item;
        Count++;
    }

    void IList.Insert(int index, object? value)
    {
        ThrowHelper.IfNullAndNullsAreIllegalThenThrow<T>(value, nameof(value));

        Insert(index, (T)value!);
    }

    private void GuardResize(int itemsToAdd = 1)
    {
        GuardDispose();
        int newCount = Count + itemsToAdd;
        if (_capacity == 0)
        {
            _array = _arrayPool.Rent(newCount);
            _capacity = _array.Length;
        }
        else if (newCount > _capacity)
        {
            int newCapacity = _capacity * 2;
            if (newCapacity == 0) newCapacity = 1;
            while (newCount > newCapacity)
            {
                newCapacity *= 2;
            }
            T[] newArray = _arrayPool.Rent(newCapacity);
            _array.CopyTo(newArray, 0);
            T[] oldArray = Interlocked.Exchange(ref _array, newArray);
            _capacity = newArray.Length;
            ClearToCount(oldArray);
            _arrayPool.Return(oldArray);
        }
    }

    public bool Remove(T item) => RemoveAtInternal(IndexOf(item), false);

    void IList.Remove(object? value)
    {
        if (IsCompatibleObject(value))
            Remove((T)value!);
    }

    public void RemoveAt(int index) => RemoveAtInternal(index, true);

    private bool RemoveAtInternal(int index, bool shouldThrow)
    {
        bool isValid = GuardIndex(index, shouldThrow);
        if (isValid)
        {
            int start = index + 1;
            if (start < Count)
            {
                _array.AsMemory(start, Count - index).CopyTo(_array.AsMemory(index));
            }

            Count--;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _array[Count] = default!;
            }
        }

        return isValid;
    }

    public void Truncate(int newLength)
    {
        GuardDispose();
        GuardIndex(newLength, allowEqualToCount: true);
        Count = newLength;
    }

    public ref T GetRef(int index)
    {
        GuardIndex(index);
        return ref _array[index];
    }

    public T this[int index]
    {
        get
        {
            GuardIndex(index);
            return _array[index];
        }
        set
        {
            GuardIndex(index);
            _array[index] = value;
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set
        {
            ThrowHelper.IfNullAndNullsAreIllegalThenThrow<T>(value, nameof(value));

            this[index] = (T)value!;
        }
    }

    private bool GuardIndex(int index, bool shouldThrow = true, bool allowEqualToCount = false)
    {
        GuardDispose();
        int count = Count;
        if ((uint)index > (uint)count || (!allowEqualToCount && index == count))
        {
            if (shouldThrow)
            {
                ThrowArgumentOutOfRangeException();
            }
            return false;
        }

        return true;

        [DoesNotReturn]
        static void ThrowArgumentOutOfRangeException()
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private static bool IsCompatibleObject(object? value) => value is T || value is null && default(T) is null;

    public static ArrayPoolList<T> Empty() => new(0);



    public void Dispose()
    {
        // Noop for empty array as sometimes this is used as part of an empty shared response.
        if (_capacity == 0) return;

        if (!_disposed)
        {
            _disposed = true;
            T[]? array = _array;
            _array = null!;
            if (array is not null)
            {
                ClearToCount(array);
                _arrayPool.Return(array);
            }
            Count = 0;
        }

#if DEBUG
        GC.SuppressFinalize(this);
#endif
    }

#if DEBUG
    private readonly StackTrace _creationStackTrace = new();

    ~ArrayPoolList()
    {
        if (_capacity != 0 && !_disposed)
        {
            throw new InvalidOperationException($"{nameof(ArrayPoolList<T>)} hasn't been disposed. Created {_creationStackTrace}");
        }
    }
#endif

    public Span<T> AsSpan() => _array.AsSpan(0, Count);
    public Memory<T> AsMemory() => new(_array, 0, Count);
    public ReadOnlyMemory<T> AsReadOnlyMemory() => new(_array, 0, Count);
    public T[] UnsafeGetInternalArray() => _array;
    public void Reverse() => AsSpan().Reverse();
}
