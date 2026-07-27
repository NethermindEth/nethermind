// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>A 64-item inline list, like <see cref="RefList16{T}"/>.</summary>
/// <remarks>
/// The sized constructor clears only exposed entries, allowing callers to use <see cref="SkipLocalsInitAttribute"/>
/// without exposing uninitialized storage.
/// </remarks>
public ref struct RefList64<T>
{
    private const int Capacity = 64;

    [InlineArray(Capacity)]
    private struct Inline64
    {
        public T? Item;
    }

    private Inline64 _array;
    public int Count { get; private set; }

    public RefList64(int initialSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialSize, Capacity);
        Count = initialSize;
        AsSpan().Clear();
    }

    public T? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) ThrowIndexOutOfRange();
            return _array[index];
        }
    }

    public Span<T> AsSpan() => MemoryMarshal.CreateSpan(ref Unsafe.As<Inline64, T>(ref _array), Count);

    public void Add(T item)
    {
        if (Count >= Capacity) throw new IndexOutOfRangeException($"Can only support a maximum of {Capacity} items");
        _array![Count++] = item;
    }

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new IndexOutOfRangeException();
}
