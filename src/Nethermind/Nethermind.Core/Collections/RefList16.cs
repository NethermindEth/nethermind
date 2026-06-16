// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// Like a list with a capacity of 16. But refstruct and therefore on the stack.
/// </summary>
/// <typeparam name="T"></typeparam>
public ref struct RefList16<T>
{
    private const int Capacity = 16;

    [InlineArray(Capacity)]
    private struct Inline16
    {
        public T? Item;
    }

    private Inline16 _array;
    public int Count { get; private set; }

    public RefList16(int initialSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialSize, Capacity);
        Count = initialSize;
    }

    public T? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) ThrowIndexOutOfRange();
            return _array[index];
        }
    }

    public Span<T> AsSpan() => MemoryMarshal.CreateSpan(ref Unsafe.As<Inline16, T>(ref _array), Count);

    public void Add(T item)
    {
        if (Count >= Capacity) throw new IndexOutOfRangeException("Can only support a maximum of 16 items");
        _array![Count++] = item;
    }

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new IndexOutOfRangeException();
}
