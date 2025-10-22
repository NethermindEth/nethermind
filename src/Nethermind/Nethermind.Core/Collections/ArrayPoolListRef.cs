using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

public ref struct ArrayPoolListRef<T>
{
    private readonly ArrayPool<T> _arrayPool;
    private T[] _array;
    private int _capacity;
    private int _count;
    private bool _disposed;

    public ArrayPoolListRef(int capacity) : this(ArrayPool<T>.Shared, capacity) { }

    public ArrayPoolListRef(int capacity, int count) : this(ArrayPool<T>.Shared, capacity, count) { }

    public ArrayPoolListRef(int capacity, IEnumerable<T> items) : this(ArrayPool<T>.Shared, capacity)
    {
        AddRange(items);
    }

    public ArrayPoolListRef(int capacity, params ReadOnlySpan<T> items) : this(ArrayPool<T>.Shared, capacity)
    {
        AddRange(items);
    }

    public ArrayPoolListRef(ReadOnlySpan<T> span) : this(ArrayPool<T>.Shared, span.Length) => AddRange(span);

    public ArrayPoolListRef(ArrayPool<T> pool, int capacity, int startingCount = 0)
    {
        System.Console.WriteLine(new StackTrace());
        _arrayPool = pool;

        if (capacity != 0)
        {
            _array = pool.Rent(capacity);
            _array.AsSpan(0, startingCount).Clear();
        }
        else
        {
            _array = [];
        }

        _capacity = _array.Length;
        _count = startingCount;
        _disposed = false;
    }

    public int Count => _count;
    public int Capacity => _capacity;
    public void Add(T item) => ArrayPoolListCore.Add(_arrayPool, ref _array, ref _capacity, ref _count, item);
    public void AddRange(params T[] items) => AddRange(items.AsSpan());
    public void AddRange(params ReadOnlySpan<T> items) => ArrayPoolListCore.AddRange(_arrayPool, ref _array, ref _capacity, ref _count, items);

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

    public void Insert(int index, T item) => ArrayPoolListCore.Insert(_arrayPool, ref _array, ref _capacity, ref _count, index, item);
    public bool Remove(T item) => ArrayPoolListCore.Remove(_array, ref _count, item);
    public void RemoveAt(int index) => ArrayPoolListCore.RemoveAt(_array, ref _count, index, shouldThrow: true);
    public void Clear() => ArrayPoolListCore.Clear(_array, ref _count);
    public void ReduceCount(int newCount) => ArrayPoolListCore.ReduceCount(_arrayPool, ref _array, ref _capacity, ref _count, newCount);
    public void Truncate(int newLength) => ArrayPoolListCore.Truncate(newLength, _array, ref _count);
    public void Sort(Comparison<T> comparison) => ArrayPoolListCore.Sort(_array, _count, comparison);
    public void Reverse() => ArrayPoolListCore.Reverse(_array, _count);
    public ref T GetRef(int index) => ref ArrayPoolListCore.GetRef(_array, index, _count);
    public Span<T> AsSpan() => _array.AsSpan(0, _count);
    public Memory<T> AsMemory() => new(_array, 0, _count);
    public ReadOnlyMemory<T> AsReadOnlyMemory() => new(_array, 0, _count);

    public T this[int index]
    {
        get => ArrayPoolListCore.Get(_array, index, _count);
        set => ArrayPoolListCore.Set(_array, index, _count, value);
    }

    public void Dispose() => ArrayPoolListCore.Dispose(_arrayPool, ref _array, ref _count, ref _capacity, ref _disposed);
    public PooledArrayEnumerator<T> GetEnumerator() => new(_array, _count);
    public bool Contains(T item) => ArrayPoolListCore.Contains(_array, item, _count);
    public int IndexOf(T item) => ArrayPoolListCore.IndexOf(_array, _count, item);
    public void CopyTo(T[] array, int arrayIndex) => ArrayPoolListCore.CopyTo(_array, _count, array, arrayIndex);

    public ArrayPoolListRef<TResult> Select<TResult>(Func<T, TResult> selector)
    {
        ArrayPoolListRef<TResult> result = new(_count);
        foreach (T item in AsSpan()) result.Add(selector(item));
        return result;
    }

    public T[] ToArray() => AsSpan().ToArray();
    public T[] UnsafeGetInternalArray() => _array;
}
