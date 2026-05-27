// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Merkleization;

internal readonly struct PooledSpan<T>(int length) : IDisposable
{
    private readonly T[] _array = ArrayPool<T>.Shared.Rent(length);

    public int Length { get; } = length;

    public ref T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Length, nameof(index));
            return ref _array[index];
        }
    }

    public Span<T> AsSpan() => _array.AsSpan(0, Length);

    public void Dispose() => ArrayPool<T>.Shared.Return(_array);

    public static implicit operator Span<T>(PooledSpan<T> value) => value.AsSpan();

    public static implicit operator ReadOnlySpan<T>(PooledSpan<T> value) => value.AsSpan();
}
