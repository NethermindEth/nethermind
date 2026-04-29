// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Absolute offset + length region within an <see cref="IHsstByteReader{TPin}"/>.
/// </summary>
public readonly record struct Bound(long Offset, int Length)
{
    public bool IsEmpty => Length == 0;
}

/// <summary>
/// No-op pin handle for readers that can return zero-copy spans (e.g. <see cref="SpanByteReader"/>).
/// </summary>
public struct NoOpPin : IDisposable
{
    public void Dispose() { }
}

/// <summary>
/// Pin handle that returns a pooled byte array on dispose. Used by copy-fallback readers
/// that rent a buffer to materialise the requested window.
/// </summary>
public struct PooledArrayPin : IDisposable
{
    private byte[]? _pooledArray;

    internal PooledArrayPin(byte[] pooledArray) => _pooledArray = pooledArray;

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
    /// Rent a pooled buffer of at least <paramref name="size"/> bytes and return a span over
    /// the first <paramref name="size"/> bytes plus a pin that returns the array on dispose.
    /// </summary>
    public static PooledArrayPin Rent(int size, out Span<byte> buffer)
    {
        byte[] arr = ArrayPool<byte>.Shared.Rent(size);
        buffer = arr.AsSpan(0, size);
        return new PooledArrayPin(arr);
    }
}

/// <summary>
/// Random-access byte source for <see cref="HsstReader{TReader,TPin}"/>, generic over the
/// pin handle type so readers can return their own zero-allocation, non-virtual pin
/// (no-op for in-memory, pooled-array for copy fallback, page refcount for paged stores, etc.).
/// </summary>
/// <typeparam name="TPin">
/// Pin handle type returned by <see cref="PinBuffer"/>. Must be a struct implementing
/// <see cref="IDisposable"/>; <c>allows ref struct</c> permits readers to return ref-struct
/// pins (e.g. ones that hold a span directly).
/// </typeparam>
public interface IHsstByteReader<TPin> where TPin : struct, IDisposable, allows ref struct
{
    long Length { get; }

    /// <summary>
    /// Copy <c>output.Length</c> bytes starting at <paramref name="offset"/> into <paramref name="output"/>.
    /// Returns false if the range is out of bounds.
    /// </summary>
    bool TryRead(long offset, scoped Span<byte> output);

    /// <summary>
    /// Pin a window of <paramref name="size"/> bytes starting at <paramref name="offset"/>.
    /// The returned span is valid until the returned pin is disposed.
    /// Span-backed implementations return a slice directly with a no-op pin; readers that can't
    /// produce a contiguous span (paged/streamed) rent a buffer, copy into it, and return a pin
    /// that releases the buffer on dispose.
    /// </summary>
    TPin PinBuffer(long offset, long size, [UnscopedRef] out ReadOnlySpan<byte> buffer);
}

/// <summary>
/// Span-backed <see cref="IHsstByteReader{TPin}"/>. Stored as a ref struct so the underlying
/// span's lifetime is tracked by the compiler — no raw pointers, no GC pinning concerns.
/// Returns <see cref="NoOpPin"/> from every <see cref="PinBuffer"/> call (zero-copy slice).
/// </summary>
public readonly ref struct SpanByteReader : IHsstByteReader<NoOpPin>
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

    public NoOpPin PinBuffer(long offset, long size, [UnscopedRef] out ReadOnlySpan<byte> buffer)
    {
        if ((ulong)offset + (ulong)size > (ulong)_data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        buffer = _data.Slice((int)offset, (int)size);
        return default;
    }
}
