using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

public ref struct ArrayPoolListRef<T>
{
    private T[] _array;
    private int _capacity;
    private int _count;

    public ArrayPoolListRef(int capacity, IEnumerable<T> items) : this(capacity) => AddRange(items);
    public ArrayPoolListRef(int capacity, params ReadOnlySpan<T> items) : this(capacity) => AddRange(items);
    public ArrayPoolListRef(ReadOnlySpan<T> span) : this(span.Length) => AddRange(span);

    public ArrayPoolListRef(int capacity, int startingCount = 0)
    {
        if (capacity != 0)
        {
            _array = ArrayPool<T>.Shared.Rent(capacity);
            _array.AsSpan(0, startingCount).Clear();
        }
        else
        {
            _array = [];
        }

        _capacity = _array.Length;
        _count = startingCount;
    }

    public readonly int Count => _count;
    public readonly int Capacity => _capacity;
    public void Add(T item) => ArrayPoolListCore.Add(ArrayPool<T>.Shared, ref _array, ref _capacity, ref _count, item);
    public void AddRange(params T[] items) => AddRange(items.AsSpan());
    public void AddRange(params ReadOnlySpan<T> items) => ArrayPoolListCore.AddRange(ArrayPool<T>.Shared, ref _array, ref _capacity, ref _count, items);

    public void AddRange(params IEnumerable<T> items)
    {
        switch (items)
        {
            case T[] array:
                AddRange((ReadOnlySpan<T>)array);
                break;
            case List<T> listItems:
                AddRange(CollectionsMarshal.AsSpan(listItems));
                break;
            default:
                {
                    foreach (T item in items)
                    {
                        Add(item);
                    }

                    break;
                }
        }
    }

    public void Insert(int index, T item) => ArrayPoolListCore.Insert(ArrayPool<T>.Shared, ref _array, ref _capacity, ref _count, index, item);
    public bool Remove(T item) => ArrayPoolListCore.Remove(_array, ref _count, item);
    public void RemoveAt(int index) => ArrayPoolListCore.RemoveAt(_array, ref _count, index, shouldThrow: true);
    public void Clear() => ArrayPoolListCore.Clear(_array, ref _count);
    public void ReduceCount(int newCount) => ArrayPoolListCore.ReduceCount(ArrayPool<T>.Shared, ref _array, ref _capacity, ref _count, newCount);
    public void Truncate(int newLength) => ArrayPoolListCore.Truncate(newLength, _array, ref _count);
    public readonly void Sort(Comparison<T> comparison) => ArrayPoolListCore.Sort(_array, _count, comparison);
    public readonly void Reverse() => ArrayPoolListCore.Reverse(_array, _count);
    public readonly ref T GetRef(int index) => ref ArrayPoolListCore.GetRef(_array, index, _count);
    public readonly Span<T> AsSpan() => _array.AsSpan(0, _count);
    public readonly Memory<T> AsMemory() => new(_array, 0, _count);
    public readonly ReadOnlyMemory<T> AsReadOnlyMemory() => new(_array, 0, _count);

    public readonly T this[int index]
    {
        get => ArrayPoolListCore.Get(_array, index, _count);
        set => ArrayPoolListCore.Set(_array, index, _count, value);
    }

    public void Dispose() => ArrayPoolListCore.Dispose(ArrayPool<T>.Shared, ref _array, ref _count, ref _capacity);
    public readonly PooledArrayEnumerator<T> GetEnumerator() => new(_array, _count);
    public readonly bool Contains(T item) => ArrayPoolListCore.Contains(_array, item, _count);
    public readonly int IndexOf(T item) => ArrayPoolListCore.IndexOf(_array, _count, item);
    public readonly void CopyTo(T[] array, int arrayIndex) => ArrayPoolListCore.CopyTo(_array, _count, array, arrayIndex);

    public readonly ArrayPoolListRef<TResult> Select<TResult>(Func<T, TResult> selector)
    {
        ArrayPoolListRef<TResult> result = new(_count);
        foreach (T item in AsSpan()) result.Add(selector(item));
        return result;
    }

    public readonly T[] ToArray() => AsSpan().ToArray();
    public readonly T[] UnsafeGetInternalArray() => _array;
}
