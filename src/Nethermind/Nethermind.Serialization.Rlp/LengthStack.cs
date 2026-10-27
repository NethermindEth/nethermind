// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Serialization.Rlp;

/// <summary>A LIFO stack of RLP lengths shared by the two passes of an <see cref="IRlpDecoder{T}"/> encode.</summary>
/// <remarks>Starts on a caller-provided buffer, typically <c>stackalloc</c>, and moves to a pooled array once that is full.</remarks>
public ref struct LengthStack(Span<int> initialBuffer) : IDisposable
{
    /// <summary>The <c>stackalloc</c> size callers start with; larger items move to a pooled array.</summary>
    public const int DefaultStackallocLength = 128;

    private Span<int> _lengths = initialBuffer;
    private int[]? _rented;
    private int _count;

    public readonly int Count => _count;

    public void Push(int length)
    {
        if (_count == _lengths.Length) Grow();
        _lengths[_count++] = length;
    }

    public int Pop() => _lengths[--_count];

    private void Grow()
    {
        int[] grown = ArrayPool<int>.Shared.Rent(Math.Max(_lengths.Length * 2, 16));
        _lengths[.._count].CopyTo(grown);
        if (_rented is not null) ArrayPool<int>.Shared.Return(_rented);
        _rented = grown;
        _lengths = grown;
    }

    public void Dispose()
    {
        if (_rented is null) return;
        ArrayPool<int>.Shared.Return(_rented);
        _rented = null;
        _lengths = default;
        _count = 0;
    }
}
