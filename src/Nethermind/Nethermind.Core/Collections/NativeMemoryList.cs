// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// List backed by <see cref="NativeMemory"/> for large buffers and <see cref="System.Buffers.ArrayPool{T}"/>
/// (pinned) for small ones — the switch is decided by byte size at allocation time. Mirrors
/// <see cref="ArrayPoolList{T}"/> in shape but keeps storage off the managed heap whenever the
/// requested capacity is large enough to be worth a native alloc. Constrained to
/// <see langword="unmanaged"/> element types. Buffers expose only <see cref="Span{T}"/> —
/// no <see cref="Memory{T}"/> projection.
/// </summary>
public sealed unsafe class NativeMemoryList<T> : IList<T>, IList, IOwnedReadOnlyList<T> where T : unmanaged
{
    private T* _ptr;
    private T[]? _pooledArray;
    private GCHandle _pinHandle;
    private int _capacity;
    private int _count;
    private bool _disposed;

    public NativeMemoryList(int capacity)
    {
        _ptr = NativeMemoryListCore<T>.AllocateBuffer(capacity, out _pooledArray, out _pinHandle, out _capacity);
        _count = 0;
    }

    public NativeMemoryList(int capacity, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, capacity);
        _ptr = NativeMemoryListCore<T>.AllocateBuffer(capacity, out _pooledArray, out _pinHandle, out _capacity);
        if (count > 0) new Span<T>(_ptr, count).Clear();
        _count = count;
    }

    public NativeMemoryList(int capacity, IEnumerable<T> enumerable) : this(capacity)
    {
        foreach (T item in enumerable) Add(item);
    }

    public NativeMemoryList(ReadOnlySpan<T> span) : this(span.Length) => AddRange(span);

    public int Count => _count;
    public int Capacity => _capacity;

    ReadOnlySpan<T> IOwnedReadOnlyList<T>.AsSpan() => AsSpan();

    public Span<T> AsSpan()
    {
        GuardDispose();
        return new Span<T>(_ptr, _count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GuardDispose()
    {
        if (_disposed) ThrowObjectDisposed();

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(NativeMemoryList<T>));
    }

    public Enumerator GetEnumerator()
    {
        GuardDispose();
        return new Enumerator(_ptr, _count);
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(T item)
    {
        GuardDispose();
        NativeMemoryListCore<T>.Add(ref _ptr, ref _capacity, ref _pooledArray, ref _pinHandle, ref _count, item);
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
        NativeMemoryListCore<T>.AddRange(ref _ptr, ref _capacity, ref _pooledArray, ref _pinHandle, ref _count, items);
    }

    public void EnsureCapacity(int capacity)
    {
        GuardDispose();
        if (capacity > _capacity)
        {
            NativeMemoryListCore<T>.GuardResize(ref _ptr, ref _capacity, ref _pooledArray, ref _pinHandle, _count, capacity - _count);
        }
    }

    public void Clear()
    {
        GuardDispose();
        NativeMemoryListCore<T>.Clear(ref _count);
    }

    public bool Contains(T item)
    {
        GuardDispose();
        return NativeMemoryListCore<T>.Contains(_ptr, _count, item);
    }

    bool IList.Contains(object? value) => IsCompatibleObject(value) && Contains((T)value!);

    public void CopyTo(T[] array, int arrayIndex)
    {
        GuardDispose();
        NativeMemoryListCore<T>.CopyTo(_ptr, _count, array, arrayIndex);
    }

    void ICollection.CopyTo(Array? array, int index)
    {
        if (array is T[] typed)
        {
            CopyTo(typed, index);
            return;
        }
        ThrowUnsupportedArrayType();

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnsupportedArrayType() =>
            throw new ArgumentException($"Only {typeof(T[])} arrays are supported.", nameof(array));
    }

    public void ReduceCount(int count)
    {
        GuardDispose();
        NativeMemoryListCore<T>.ReduceCount(ref _ptr, ref _capacity, ref _pooledArray, ref _pinHandle, ref _count, count);
    }

    public void Sort(Comparison<T> comparison)
    {
        GuardDispose();
        NativeMemoryListCore<T>.Sort(_ptr, _count, comparison);
    }

    public void Sort<TComparer>(TComparer comparer) where TComparer : IComparer<T>
    {
        GuardDispose();
        NativeMemoryListCore<T>.Sort(_ptr, _count, comparer);
    }

    public void Reverse()
    {
        GuardDispose();
        NativeMemoryListCore<T>.Reverse(_ptr, _count);
    }

    bool IList.IsFixedSize => false;
    bool ICollection<T>.IsReadOnly => false;
    bool IList.IsReadOnly => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    public int IndexOf(T item)
    {
        GuardDispose();
        return NativeMemoryListCore<T>.IndexOf(_ptr, _count, item);
    }

    int IList.IndexOf(object? value) => IsCompatibleObject(value) ? IndexOf((T)value!) : -1;

    public void Insert(int index, T item)
    {
        GuardDispose();
        NativeMemoryListCore<T>.Insert(ref _ptr, ref _capacity, ref _pooledArray, ref _pinHandle, ref _count, index, item);
    }

    void IList.Insert(int index, object? value)
    {
        ThrowHelper.IfNullAndNullsAreIllegalThenThrow<T>(value, nameof(value));
        Insert(index, (T)value!);
    }

    public bool Remove(T item)
    {
        GuardDispose();
        return NativeMemoryListCore<T>.Remove(_ptr, ref _count, item);
    }

    void IList.Remove(object? value)
    {
        if (IsCompatibleObject(value)) Remove((T)value!);
    }

    public void RemoveAt(int index)
    {
        GuardDispose();
        NativeMemoryListCore<T>.RemoveAt(_ptr, ref _count, index, shouldThrow: true);
    }

    public void Truncate(int newLength)
    {
        GuardDispose();
        NativeMemoryListCore<T>.Truncate(newLength, ref _count);
    }

    public ref T GetRef(int index)
    {
        GuardDispose();
        return ref NativeMemoryListCore<T>.GetRef(_ptr, index, _count);
    }

    public T this[int index]
    {
        get
        {
            GuardDispose();
            return NativeMemoryListCore<T>.Get(_ptr, index, _count);
        }
        set
        {
            GuardDispose();
            NativeMemoryListCore<T>.Set(_ptr, index, _count, value);
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

    private static bool IsCompatibleObject(object? value) => value is T;

    public static NativeMemoryList<T> Empty() => new(0);

    public void Dispose()
    {
        NativeMemoryListCore<T>.Dispose(ref _ptr, ref _pooledArray, ref _pinHandle, ref _count, ref _capacity, ref _disposed);
        GC.SuppressFinalize(this);
    }

#if DEBUG
    private readonly StackTrace _creationStackTrace = new();
#endif

    ~NativeMemoryList()
    {
        if (_capacity != 0 && !_disposed)
        {
#if DEBUG
            Console.Error.WriteLine($"Warning: {nameof(NativeMemoryList<T>)} was not disposed. Created at: {_creationStackTrace}");
#endif
            // Always free unmanaged memory / return pooled array in the finalizer to avoid
            // process-lifetime native leaks or starvation of the ArrayPool.
            NativeMemoryListCore<T>.Dispose(ref _ptr, ref _pooledArray, ref _pinHandle, ref _count, ref _capacity, ref _disposed);
        }
    }

    public struct Enumerator(T* ptr, int count) : IEnumerator<T>
    {
        private int _index = -1;

        public bool MoveNext() => ++_index < count;
        public void Reset() => _index = -1;
        public readonly T Current => ptr[_index];
        readonly object IEnumerator.Current => Current!;
        public readonly void Dispose() { }
    }
}
