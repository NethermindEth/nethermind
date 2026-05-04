// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// Ref-struct list backed by <see cref="NativeMemory"/>. Mirrors <see cref="ArrayPoolListRef{T}"/>
/// but allocates off the managed heap. Constrained to <see langword="unmanaged"/> element types.
/// Native buffers expose only <see cref="Span{T}"/> — no <see cref="Memory{T}"/> projection.
/// </summary>
public unsafe ref struct NativeMemoryListRef<T> where T : unmanaged
{
    private T* _ptr;
    private int _capacity;
    private int _count;

    public NativeMemoryListRef(int capacity, IEnumerable<T> items) : this(capacity) => AddRange(items);
    public NativeMemoryListRef(int capacity, params ReadOnlySpan<T> items) : this(capacity) => AddRange(items);
    public NativeMemoryListRef(ReadOnlySpan<T> span) : this(span.Length) => AddRange(span);

    public NativeMemoryListRef(int capacity, int startingCount = 0)
    {
        if (capacity != 0)
        {
            _ptr = (T*)NativeMemory.Alloc((nuint)capacity, (nuint)sizeof(T));
            new Span<T>(_ptr, startingCount).Clear();
        }
        else
        {
            _ptr = null;
        }
        _capacity = capacity;
        _count = startingCount;
    }

    public readonly int Count => _count;
    public readonly int Capacity => _capacity;

    public void Add(T item) => NativeMemoryListCore<T>.Add(ref _ptr, ref _capacity, ref _count, item);
    public void AddRange(params T[] items) => AddRange(items.AsSpan());
    public void AddRange(params ReadOnlySpan<T> items) => NativeMemoryListCore<T>.AddRange(ref _ptr, ref _capacity, ref _count, items);

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
                foreach (T item in items) Add(item);
                break;
        }
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity > _capacity)
        {
            NativeMemoryListCore<T>.GuardResize(ref _ptr, ref _capacity, _count, capacity - _count);
        }
    }

    public void Insert(int index, T item) => NativeMemoryListCore<T>.Insert(ref _ptr, ref _capacity, ref _count, index, item);
    public bool Remove(T item) => NativeMemoryListCore<T>.Remove(_ptr, ref _count, item);
    public T? RemoveLast() => NativeMemoryListCore<T>.RemoveLast(_ptr, ref _count);
    public void RemoveAt(int index) => NativeMemoryListCore<T>.RemoveAt(_ptr, ref _count, index, shouldThrow: true);
    public void Clear() => NativeMemoryListCore<T>.Clear(ref _count);
    public void ReduceCount(int newCount) => NativeMemoryListCore<T>.ReduceCount(ref _ptr, ref _capacity, ref _count, newCount);
    public void Truncate(int newLength) => NativeMemoryListCore<T>.Truncate(newLength, ref _count);
    public readonly void Sort(Comparison<T> comparison) => NativeMemoryListCore<T>.Sort(_ptr, _count, comparison);
    public readonly void Sort<TComparer>(TComparer comparer) where TComparer : IComparer<T> => NativeMemoryListCore<T>.Sort(_ptr, _count, comparer);
    public readonly void Reverse() => NativeMemoryListCore<T>.Reverse(_ptr, _count);
    public readonly ref T GetRef(int index) => ref NativeMemoryListCore<T>.GetRef(_ptr, index, _count);
    public readonly Span<T> AsSpan() => new(_ptr, _count);

    public readonly T this[int index]
    {
        get => NativeMemoryListCore<T>.Get(_ptr, index, _count);
        set => NativeMemoryListCore<T>.Set(_ptr, index, _count, value);
    }

    public void Dispose() => NativeMemoryListCore<T>.Dispose(ref _ptr, ref _count, ref _capacity);

    public readonly bool Contains(T item) => NativeMemoryListCore<T>.Contains(_ptr, _count, item);
    public readonly int IndexOf(T item) => NativeMemoryListCore<T>.IndexOf(_ptr, _count, item);
    public readonly void CopyTo(T[] array, int arrayIndex) => NativeMemoryListCore<T>.CopyTo(_ptr, _count, array, arrayIndex);
    public readonly T[] ToArray() => AsSpan().ToArray();
}
