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

    // Not readonly: the inline RefList64 is a mutable value whose AsSpan must reach the real field.
    // A readonly getter would defensively copy it and hand back a span over the throwaway copy.
    public Span<T> Span => _rented is null ? _inline.AsSpan() : _rented.AsSpan(0, _length);

    public void Dispose()
    {
        if (_rented is not { } rented) return;

        rented.AsSpan(0, _length).Clear();
        ArrayPool<T>.Shared.Return(rented);
        _rented = null;
    }
}

/// <summary>
/// <inheritdoc cref="PbtFrameBuffer{T}" path="/summary"/> For elements that hold a lease, which
/// disposal releases.
/// </summary>
/// <remarks>
/// The frame settles each slot it fills, taking the leases back as it goes and leaving the slot
/// <c>default</c>, so on the way out there is normally nothing here to release. What this covers is the
/// frame that never gets there: a descent unwound by a throwing sibling would otherwise abandon every
/// lease its slots hold, since nothing below the throw runs again.
/// </remarks>
internal ref struct PbtLeasedFrameBuffer<T>(int length) where T : struct, IDisposable
{
    private PbtFrameBuffer<T> _buffer = new(length);

    /// <inheritdoc cref="PbtFrameBuffer{T}.Span"/>
    public Span<T> Span => _buffer.Span;

    public void Dispose()
    {
        foreach (ref T element in _buffer.Span) element.Dispose();
        _buffer.Dispose();
    }
}
