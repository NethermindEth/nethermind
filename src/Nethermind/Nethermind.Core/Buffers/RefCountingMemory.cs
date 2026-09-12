// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core.Utils;
using RefCountingMemoryMetrics = Nethermind.Core.Buffers.Metrics.Metrics;

namespace Nethermind.Core.Buffers;

/// <summary>
/// A ref-counted <see cref="MemoryManager{T}"/> over byte memory. Each <see cref="AcquireLease"/>
/// hands out one reference and each <see cref="IDisposable.Dispose"/> releases one; the last release
/// runs cleanup exactly once. An <see cref="Owning"/> instance returns its buffer to
/// <see cref="ArrayPool{T}.Shared"/> on that last release, an <see cref="OwningRocksDb"/> instance
/// disposes its memory manager, and a <see cref="Wrapping"/> instance leaves its array untouched.
/// </summary>
/// <remarks>
/// The lease counter is lock-free via <see cref="RefCountingLease"/>, so leases may be acquired and
/// released from multiple threads. Pinning is unsupported (<see cref="Pin"/>/<see cref="Unpin"/> are
/// no-ops, mirroring <see cref="ArrayMemoryManager"/>): consumers read through <see cref="GetSpan"/>.
/// </remarks>
public sealed class RefCountingMemory : MemoryManager<byte>
{
    internal enum BackingKind
    {
        Pooled,
        Wrapped,
        RocksDb,
    }

    private readonly byte[]? _buffer;
    private readonly MemoryManager<byte>? _owner;
    private readonly int _capacity;
    private int _length;
    private readonly BackingKind _backingKind;
    private long _leases = RefCountingLease.Single;

    private RefCountingMemory(byte[] buffer, int length, BackingKind backingKind)
    {
        _buffer = buffer;
        _capacity = buffer.Length;
        _length = length;
        _backingKind = backingKind;
        RefCountingMemoryMetrics.ReportRefCountingMemoryAllocation(backingKind, _capacity);
    }

    private RefCountingMemory(MemoryManager<byte> owner)
    {
        _owner = owner;
        _capacity = owner.GetSpan().Length;
        _length = _capacity;
        _backingKind = BackingKind.RocksDb;
        RefCountingMemoryMetrics.ReportRefCountingMemoryAllocation(_backingKind, _capacity);
    }

    /// <summary>
    /// Wraps a buffer rented from <see cref="ArrayPool{T}.Shared"/> (possibly oversized, so the value
    /// occupies its first <paramref name="length"/> bytes); the last release returns it to the pool.
    /// </summary>
    public static RefCountingMemory Owning(byte[] pooledBuffer, int length) => new(pooledBuffer, length, BackingKind.Pooled);

    /// <summary>
    /// Adopts memory owned by RocksDB without copying it; the last release disposes its manager.
    /// </summary>
    /// <param name="owner">The manager whose memory and lifetime are adopted.</param>
    /// <returns>Ref-counted access to the adopted memory.</returns>
    public static RefCountingMemory OwningRocksDb(MemoryManager<byte> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new RefCountingMemory(owner);
    }

    /// <summary>Wraps an array whose lifetime is owned elsewhere; the last release does not free it.</summary>
    public static RefCountingMemory Wrapping(byte[] array) => new(array, array.Length, BackingKind.Wrapped);

    public static RefCountingMemory? WrappingOrNull(byte[]? array) => array is null ? null : Wrapping(array);

    /// <summary>
    /// Copies the content into a fresh array, consuming the caller's lease; the memory must not be used afterwards.
    /// </summary>
    public byte[] ToArrayAndRelease()
    {
        using (this) return GetSpan().ToArray();
    }

    /// <summary>
    /// Narrows the value to its first <paramref name="length"/> bytes, for a producer that rented more
    /// room than it turned out to need.
    /// </summary>
    /// <remarks>
    /// Only the producer may call this, before the memory is shared: it changes what every reader sees,
    /// so a second lease being out already means someone is reading the bytes it would cut away. The
    /// backing buffer is unaffected — a pooled one is still returned whole.
    /// </remarks>
    public void Shrink(int length)
    {
        Debug.Assert((uint)length <= (uint)_length, "a shrink only ever narrows the value");
        Debug.Assert(Volatile.Read(ref _leases) == RefCountingLease.Single, "the value is already shared, so its readers would see it change");
        _length = length;
    }

    /// <summary>Acquires one additional reference; the matching <see cref="IDisposable.Dispose"/> releases it.</summary>
    /// <exception cref="InvalidOperationException">The memory is already being torn down.</exception>
    public void AcquireLease()
    {
        if (!RefCountingLease.TryAcquire(ref _leases)) throw new InvalidOperationException("The lease cannot be acquired");
    }

    public override Span<byte> GetSpan() => _backingKind is BackingKind.RocksDb
        ? _owner!.GetSpan()[.._length]
        : _buffer.AsSpan(0, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => default;

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        if (!RefCountingLease.ReleaseOnce(ref _leases)) return;

        try
        {
            if (_backingKind is BackingKind.Pooled) ArrayPool<byte>.Shared.Return(_buffer!);
            else if (_backingKind is BackingKind.RocksDb) ((IDisposable)_owner!).Dispose();
        }
        finally
        {
            RefCountingMemoryMetrics.ReportRefCountingMemoryRelease(_backingKind, _capacity);
        }
    }
}
