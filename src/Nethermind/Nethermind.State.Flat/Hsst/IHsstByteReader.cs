// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Absolute offset + length region within an <see cref="IHsstByteReader"/>.
/// </summary>
public readonly record struct Bound(long Offset, int Length)
{
    public bool IsEmpty => Length == 0;
}

/// <summary>
/// Disposable handle returned by <see cref="IHsstByteReader.PinBuffer"/>. Releases the pin
/// (e.g. returns a pooled scratch buffer) when disposed. <see cref="None"/> is a no-op handle
/// for span-backed readers that do zero-copy pins.
/// </summary>
public struct BufferPin : IDisposable
{
    private byte[]? _pooledArray;

    internal BufferPin(byte[] pooledArray) => _pooledArray = pooledArray;

    public static BufferPin None => default;

    public void Dispose()
    {
        byte[]? arr = _pooledArray;
        if (arr is not null)
        {
            _pooledArray = null;
            ArrayPool<byte>.Shared.Return(arr);
        }
    }

    /// <summary>
    /// Helper for copy-fallback readers: rents a pooled buffer of at least <paramref name="size"/>
    /// bytes and returns a span over the first <paramref name="size"/> bytes plus a pin that
    /// returns the array on dispose.
    /// </summary>
    public static BufferPin RentForCopy(int size, out Span<byte> buffer)
    {
        byte[] arr = ArrayPool<byte>.Shared.Rent(size);
        buffer = arr.AsSpan(0, size);
        return new BufferPin(arr);
    }
}

/// <summary>
/// Random-access byte source for <see cref="HsstReader{TReader}"/>.
/// Supports both copy-based <see cref="TryRead"/> (small reads) and
/// <see cref="PinBuffer"/> (zero-copy span when the backing store can produce one).
/// </summary>
public interface IHsstByteReader
{
    long Length { get; }

    /// <summary>
    /// Copy <c>output.Length</c> bytes starting at <paramref name="offset"/> into <paramref name="output"/>.
    /// Returns false if the range is out of bounds.
    /// </summary>
    bool TryRead(long offset, scoped Span<byte> output);

    /// <summary>
    /// Pin a window of <paramref name="size"/> bytes starting at <paramref name="offset"/>.
    /// The returned span is valid until the returned <see cref="BufferPin"/> is disposed.
    /// Span-backed implementations return a slice directly with a no-op pin; readers that can't
    /// produce a contiguous span (paged/streamed) rent a buffer, copy into it, and return a pin
    /// that releases the buffer on dispose.
    /// </summary>
    BufferPin PinBuffer(long offset, long size, [UnscopedRef] out ReadOnlySpan<byte> buffer);
}

/// <summary>
/// Span-backed <see cref="IHsstByteReader"/>. Stored as a ref struct so the underlying span's
/// lifetime is tracked by the compiler — no raw pointers, no GC pinning concerns.
/// </summary>
public readonly ref struct SpanByteReader : IHsstByteReader
{
    private readonly ReadOnlySpan<byte> _data;

    public SpanByteReader(ReadOnlySpan<byte> data) => _data = data;

    public long Length => _data.Length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset > (ulong)(_data.Length - output.Length)) return false;
        _data.Slice((int)offset, output.Length).CopyTo(output);
        return true;
    }

    public BufferPin PinBuffer(long offset, long size, [UnscopedRef] out ReadOnlySpan<byte> buffer)
    {
        if ((ulong)offset + (ulong)size > (ulong)_data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        buffer = _data.Slice((int)offset, (int)size);
        return BufferPin.None;
    }
}
