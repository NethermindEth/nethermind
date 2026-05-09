// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Absolute offset + length region within an <see cref="IHsstByteReader{TPin}"/>.
/// </summary>
public readonly record struct Bound(long Offset, long Length)
{
    public bool IsEmpty => Length == 0;
}

/// <summary>
/// Pin handle returned by <see cref="IHsstByteReader{TPin}.PinBuffer"/>: combines a
/// disposable release primitive with the pinned <see cref="Buffer"/> span itself.
/// Pin types are ref structs so the buffer's lifetime is tracked by the compiler.
/// </summary>
public interface IBufferPin : IDisposable
{
    ReadOnlySpan<byte> Buffer { get; }
}

/// <summary>
/// No-op pin for readers that can return zero-copy spans (e.g. <see cref="SpanByteReader"/>):
/// holds the span directly, no release work.
/// </summary>
public readonly ref struct NoOpPin(ReadOnlySpan<byte> buffer) : IBufferPin
{
    public ReadOnlySpan<byte> Buffer { get; } = buffer;
    public void Dispose() { }
}

/// <summary>
/// Pin that returns a pooled byte array on dispose. Used by copy-fallback readers
/// that rent a buffer to materialise the requested window.
/// </summary>
public ref struct PooledArrayPin : IBufferPin
{
    private byte[]? _pooledArray;
    private readonly int _size;

    private PooledArrayPin(byte[] pooledArray, int size)
    {
        _pooledArray = pooledArray;
        _size = size;
    }

    public readonly ReadOnlySpan<byte> Buffer => _pooledArray.AsSpan(0, _size);

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
        return new PooledArrayPin(arr, size);
    }
}

/// <summary>
/// Random-access byte source for <see cref="HsstReader{TReader,TPin}"/>, generic over the
/// pin handle type so readers can return their own zero-allocation, non-virtual pin
/// (no-op for in-memory, pooled-array for copy fallback, page refcount for paged stores, etc.).
/// The pinned buffer is exposed via <see cref="IBufferPin.Buffer"/>.
/// </summary>
/// <typeparam name="TPin">
/// Pin handle type returned by <see cref="PinBuffer"/>. Must be a struct implementing
/// <see cref="IBufferPin"/>; <c>allows ref struct</c> permits readers to return ref-struct
/// pins (e.g. ones that hold a span directly).
/// </typeparam>
public interface IHsstByteReader<TPin> where TPin : struct, IBufferPin, allows ref struct
{
    long Length { get; }

    /// <summary>The full extent of this reader as a <see cref="Bound"/> — i.e. <c>(0, Length)</c>.</summary>
    Bound Bound { get; }

    /// <summary>
    /// Copy <c>output.Length</c> bytes starting at <paramref name="offset"/> into <paramref name="output"/>.
    /// Returns false if the range is out of bounds.
    /// </summary>
    bool TryRead(long offset, scoped Span<byte> output);

    /// <summary>
    /// Pin a window of <paramref name="size"/> bytes starting at <paramref name="offset"/>.
    /// The pinned bytes are accessed via <see cref="IBufferPin.Buffer"/> and remain valid until
    /// the returned pin is disposed.
    /// </summary>
    TPin PinBuffer(long offset, long size);
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

    public Bound Bound => new(0, _data.Length);

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset > (ulong)(_data.Length - output.Length)) return false;
        _data.Slice((int)offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(long offset, long size)
    {
        if ((ulong)offset + (ulong)size > (ulong)_data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new NoOpPin(_data.Slice((int)offset, (int)size));
    }
}
