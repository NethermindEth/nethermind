// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Utils;

namespace Nethermind.Core.Buffers;

/// <summary>
/// A ref-counted <see cref="MemoryManager{T}"/> over a byte array. Each <see cref="AcquireLease"/>
/// hands out one reference and each <see cref="IDisposable.Dispose"/> releases one; the last release
/// runs cleanup exactly once. An <see cref="Owning"/> instance returns its buffer to
/// <see cref="ArrayPool{T}.Shared"/> on that last release, so a value rented from the pool is
/// recycled once every reader is done; a <see cref="Wrapping"/> instance leaves its array untouched,
/// for buffers whose lifetime is owned elsewhere (e.g. arrays retained in a diff layer).
/// </summary>
/// <remarks>
/// The lease counter is lock-free via <see cref="RefCountingLease"/>, so leases may be acquired and
/// released from multiple threads. Pinning is unsupported (<see cref="Pin"/>/<see cref="Unpin"/> are
/// no-ops, mirroring <see cref="ArrayMemoryManager"/>): consumers read through <see cref="GetSpan"/>.
/// </remarks>
public sealed class RefCountingMemory : MemoryManager<byte>
{
    private readonly byte[] _buffer;
    private readonly int _length;
    private readonly bool _pooled;
    private long _leases = RefCountingLease.Single;

    private RefCountingMemory(byte[] buffer, int length, bool pooled)
    {
        _buffer = buffer;
        _length = length;
        _pooled = pooled;
    }

    /// <summary>
    /// Wraps a buffer rented from <see cref="ArrayPool{T}.Shared"/> (possibly oversized, so the value
    /// occupies its first <paramref name="length"/> bytes); the last release returns it to the pool.
    /// </summary>
    public static RefCountingMemory Owning(byte[] pooledBuffer, int length) => new(pooledBuffer, length, pooled: true);

    /// <summary>Wraps an array whose lifetime is owned elsewhere; the last release does not free it.</summary>
    public static RefCountingMemory Wrapping(byte[] array) => new(array, array.Length, pooled: false);

    /// <summary>As <see cref="Wrapping"/>, returning <c>null</c> when <paramref name="array"/> is <c>null</c>.</summary>
    public static RefCountingMemory? WrappingOrNull(byte[]? array) => array is null ? null : Wrapping(array);

    /// <summary>
    /// Copies the content into a fresh array and releases this memory, the inverse of
    /// <see cref="WrappingOrNull"/> for a consumer that keeps its values as arrays.
    /// </summary>
    /// <remarks>
    /// Consumes the caller's lease, so the memory must not be used afterwards. A consumer holding an
    /// optional value calls it through <c>?.</c>, which copies <c>null</c> to <c>null</c>.
    /// </remarks>
    public byte[] ToArrayAndRelease()
    {
        using (this) return GetSpan().ToArray();
    }

    /// <summary>Acquires one additional reference; the matching <see cref="IDisposable.Dispose"/> releases it.</summary>
    /// <exception cref="InvalidOperationException">The memory is already being torn down.</exception>
    public void AcquireLease()
    {
        if (!RefCountingLease.TryAcquire(ref _leases)) throw new InvalidOperationException("The lease cannot be acquired");
    }

    public override Span<byte> GetSpan() => _buffer.AsSpan(0, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => default;

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        if (RefCountingLease.ReleaseOnce(ref _leases) && _pooled) ArrayPool<byte>.Shared.Return(_buffer);
    }
}
