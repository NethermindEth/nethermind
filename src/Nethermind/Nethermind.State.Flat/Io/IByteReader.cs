// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Io;

/// <summary>
/// Absolute offset + length region within an <see cref="IByteReader{TPin}"/>.
/// </summary>
public readonly record struct Bound(long Offset, long Length);

/// <summary>
/// Pin handle returned by <see cref="IByteReader{TPin}.PinBuffer"/>: combines a
/// disposable release primitive with the pinned <see cref="Buffer"/> span itself.
/// Implementations may be ref structs so the buffer's lifetime is tracked by the compiler.
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
/// Random-access byte source over a fixed region, generic over the
/// pin handle type so readers can return their own zero-allocation, non-virtual pin
/// (no-op for in-memory, pooled-array for copy fallback, page refcount for paged stores, etc.).
/// The pinned buffer is exposed via <see cref="IBufferPin.Buffer"/>.
/// </summary>
/// <typeparam name="TPin">
/// Pin handle type returned by <see cref="PinBuffer"/>. Must be a struct implementing
/// <see cref="IBufferPin"/>; <c>allows ref struct</c> permits readers to return ref-struct
/// pins (e.g. ones that hold a span directly).
/// </typeparam>
public interface IByteReader<TPin> where TPin : struct, IBufferPin, allows ref struct
{
    long Length { get; }

    /// <summary>
    /// Copy <c>output.Length</c> bytes starting at <paramref name="offset"/> into <paramref name="output"/>.
    /// Returns false if the range is out of bounds.
    /// </summary>
    bool TryRead(long offset, scoped Span<byte> output);

    /// <summary>
    /// Pin the window described by <paramref name="bound"/> (absolute offset + length).
    /// The pinned bytes are accessed via <see cref="IBufferPin.Buffer"/> and remain valid until
    /// the returned pin is disposed.
    /// </summary>
    TPin PinBuffer(Bound bound);
}
