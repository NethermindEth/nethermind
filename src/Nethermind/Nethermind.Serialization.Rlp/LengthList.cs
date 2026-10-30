// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

/// <summary>The RLP container lengths shared by the two passes of an <see cref="IRlpDecoder{T}"/> encode.</summary>
/// <remarks>
/// Populating writes through the <see cref="LengthListWriter"/> from <see cref="BeginRootContainer"/>, and encoding
/// <see cref="Read"/>s the lengths back in the same order.
/// Starts on a caller-provided buffer, typically <c>stackalloc</c>, and moves to a pooled array once that is full.
/// </remarks>
public ref struct LengthList(Span<int> initialBuffer) : IDisposable
{
    /// <summary>The <c>stackalloc</c> size callers start with; larger items move to a pooled array.</summary>
    public const int DefaultStackallocLength = 16;

    /// <summary>The largest <c>stackalloc</c> size an initial size is clamped to.</summary>
    public const int MaxStackallocLength = 64;

    private readonly Span<int> _initialBuffer = initialBuffer;
    private LengthListStorage _storage;
    private int _position;

    /// <summary>The number of populated lengths not read yet.</summary>
    public readonly int UnreadCount => _storage.Count - _position;

    /// <summary>Clears the list and returns the writer that accumulates the total length of what is populated through it.</summary>
    /// <remarks>The total takes the first slot, so reading starts at the first container begun under the root.</remarks>
    [UnscopedRef]
    public LengthListWriter BeginRootContainer()
    {
        _storage.Count = 0;
        _position = 1;
        return new LengthListWriter(_initialBuffer, ref _storage, _storage.Reserve(_initialBuffer), parentIndex: -1);
    }

    /// <summary>Reads the next content length, in the order the containers were begun.</summary>
    public int Read() => _storage.Lengths(_initialBuffer)[_position++];

    public void Dispose()
    {
        if (_storage.Rented is null) return;
        ArrayPool<int>.Shared.Return(_storage.Rented);
        _storage = default;
        _position = 0;
    }
}

/// <summary>The growable part of a <see cref="LengthList"/>, which every <see cref="LengthListWriter"/> of the list refers to.</summary>
internal struct LengthListStorage
{
    public int[]? Rented;
    public int Count;

    public readonly Span<int> Lengths(Span<int> initialBuffer) => Rented is null ? initialBuffer : Rented;

    public int Reserve(Span<int> initialBuffer)
    {
        Span<int> lengths = Lengths(initialBuffer);
        if (Count == lengths.Length) lengths = Grow(lengths);
        lengths[Count] = 0;
        return Count++;
    }

    private Span<int> Grow(Span<int> lengths)
    {
        int[] grown = ArrayPool<int>.Shared.Rent(Math.Max(lengths.Length * 2, LengthList.DefaultStackallocLength));
        lengths[..Count].CopyTo(grown);
        if (Rented is not null) ArrayPool<int>.Shared.Return(Rented);
        Rented = grown;
        return grown;
    }
}
