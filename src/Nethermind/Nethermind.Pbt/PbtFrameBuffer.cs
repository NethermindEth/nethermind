// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Pbt;

/// <summary>
/// Scoped frame storage that keeps legacy tile buffers inline and rents wider buffers without
/// embedding their reference-bearing elements in every recursive frame.
/// </summary>
internal ref struct PbtFrameBuffer<T>
{
    private RefList64<T> _inline;
    private T[]? _rented;
    private readonly int _length;

    public PbtFrameBuffer(int length)
    {
        _length = length;
        if (length <= 64)
        {
            _inline = new RefList64<T>(length);
            _rented = null;
        }
        else
        {
            _inline = default;
            _rented = ArrayPool<T>.Shared.Rent(length);
            _rented.AsSpan(0, length).Clear();
        }
    }

    public readonly Span<T> Span => _rented is null ? _inline.AsSpan() : _rented.AsSpan(0, _length);

    public void Dispose()
    {
        if (_rented is not { } rented) return;

        rented.AsSpan(0, _length).Clear();
        ArrayPool<T>.Shared.Return(rented);
        _rented = null;
    }
}
