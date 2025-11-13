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
    private int _count = 0;

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

        _count = startingCount;
    }

    public int Count => _count;

    ReadOnlySpan<T> IOwnedReadOnlyList<T>.AsSpan() => AsSpan();

    public PooledArrayEnumerator<T> GetEnumerator()
    {
        GuardDispose();
        return new PooledArrayEnumerator<T>(_array, _count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GuardDispose()
    {
        if (_disposed)
        {
            ThrowObjectDisposed();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowObjectDisposed()
        {
            throw new ObjectDisposedException(nameof(ArrayPoolList<T>));
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(T item)
    {
        GuardDispose();
        ArrayPoolListCore.Add(_arrayPool, ref _array, ref _capacity, ref _count, item);
    }

    int IList.Add(object? value)
    {
        ThrowHelper.IfNullAndNullsAreIllegalThenThrow<T>(value, nameof(value));

        Add((T)value!);

        return _count - 1;
    }

    public void AddRange(params ReadOnlySpan<T> items)
    {
        GuardDispose();
        ArrayPoolListCore.AddRange(_arrayPool, ref _array, ref _capacity, ref _count, items);
    }

    public void Clear()
    {
        GuardDispose();
        ArrayPoolListCore.Clear(_array, ref _count);
    }

    public bool Contains(T item)
    {
        GuardDispose();
        return ArrayPoolListCore.Contains(_array, item, _count);
    }

    bool IList.Contains(object? value) => IsCompatibleObject(value) && Contains((T)value!);

    public void CopyTo(T[] array, int arrayIndex)
    {
        GuardDispose();
        ArrayPoolListCore.CopyTo(_array, _count, array, arrayIndex);
    }

    void ICollection.CopyTo(Array? array, int index)
    {
        if (array is not null && array.Rank != 1)
            ThrowMultiDimensionalArray();

        GuardDispose();

        Array.Copy(_array, 0, array!, index, _count);

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowMultiDimensionalArray()
        {
            throw new ArgumentException("Only single dimensional arrays are supported.", nameof(array));
        }
    }

    public void ReduceCount(int count)
    {
        GuardDispose();
        ArrayPoolListCore.ReduceCount(_arrayPool, ref _array, ref _capacity, ref _count, count);
    }

    public void Sort(Comparison<T> comparison)
    {
        GuardDispose();
        ArrayPoolListCore.Sort(_array, _count, comparison);
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
        return ArrayPoolListCore.IndexOf(_array, _count, item);
    }

    int IList.IndexOf(object? value) => IsCompatibleObject(value) ? IndexOf((T)value!) : -1;

    public void Insert(int index, T item)
    {
        GuardDispose();
        ArrayPoolListCore.Insert(_arrayPool, ref _array, ref _capacity, ref _count, index, item);
    }

    void IList.Insert(int index, object? value)
    {
        ThrowHelper.IfNullAndNullsAreIllegalThenThrow<T>(value, nameof(value));

        Insert(index, (T)value!);
    }

    public bool Remove(T item)
    {
        GuardDispose();
        return ArrayPoolListCore.Remove(_array, ref _count, item);
    }

    void IList.Remove(object? value)
    {
        if (IsCompatibleObject(value))
            Remove((T)value!);
    }

    public void RemoveAt(int index)
    {
        GuardDispose();
        ArrayPoolListCore.RemoveAt(_array, ref _count, index, true);
    }

    public void Truncate(int newLength)
    {
        GuardDispose();
        ArrayPoolListCore.Truncate(newLength, _array, ref _count);
    }

    public ref T GetRef(int index)
    {
        GuardDispose();
        return ref ArrayPoolListCore.GetRef(_array, index, _count);
    }

    public T this[int index]
    {
        get
        {
            GuardDispose();
            return ArrayPoolListCore.Get(_array, index, _count);
        }
        set
        {
            GuardDispose();
            ArrayPoolListCore.Set(_array, index, _count, value);
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


    private static bool IsCompatibleObject(object? value) => value is T || value is null && default(T) is null;

    public static ArrayPoolList<T> Empty() => new(0);

    public void Dispose()
    {
        ArrayPoolListCore.Dispose(_arrayPool, ref _array, ref _count, ref _capacity, ref _disposed);

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
            Console.Error.WriteLine($"Warning: {nameof(ArrayPoolList<T>)} was not disposed. Created at: {_creationStackTrace}");
        }
    }
#endif

    public Span<T> AsSpan()
    {
        GuardDispose();
        return _array.AsSpan(0, _count);
    }
    public Memory<T> AsMemory()
    {
        GuardDispose();
        return new(_array, 0, _count);
    }
    public ReadOnlyMemory<T> AsReadOnlyMemory()
    {
        GuardDispose();
        return new(_array, 0, _count);
    }
    public T[] UnsafeGetInternalArray()
    {
        GuardDispose();
        return _array;
    }
    public void Reverse()
    {
        GuardDispose();
        ArrayPoolListCore.Reverse(_array, _count);
    }
}
