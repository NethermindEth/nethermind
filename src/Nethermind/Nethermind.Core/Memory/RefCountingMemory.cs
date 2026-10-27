// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Utils;
using RefCountingMemoryMetrics = Nethermind.Core.Memory.Metrics.Metrics;

namespace Nethermind.Core.Memory;

/// <summary>
/// A ref-counted <see cref="MemoryManager{T}"/> over byte memory. Each <see cref="AcquireLease"/>
/// hands out one reference and each <see cref="IDisposable.Dispose"/> releases one; the last release
/// runs cleanup exactly once. An <see cref="Owning"/> instance returns its buffer to
/// <see cref="ArrayPool{T}.Shared"/> on that last release, an <see cref="OwningRocksDb"/> instance
/// disposes its memory manager, and a <see cref="Wrapping"/> instance leaves its array to the GC.
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
        RocksDb,
        Managed,
    }

    private readonly byte[]? _buffer;
    private readonly MemoryManager<byte>? _owner;
    private readonly int _capacity;
    private readonly int _length;
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

    /// <summary>Wraps a managed array that the GC owns; the last release does nothing to it.</summary>
    public static RefCountingMemory Wrapping(byte[] buffer) => new(buffer, buffer.Length, BackingKind.Managed);

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

    /// <summary>Gets the size of the backing buffer, which a pooled buffer may have rented larger than the value.</summary>
    public int Capacity => _capacity;

    /// <summary>Acquires one additional reference; the matching <see cref="IDisposable.Dispose"/> releases it.</summary>
    /// <exception cref="InvalidOperationException">The memory is already being torn down.</exception>
    public void AcquireLease()
    {
        if (!TryAcquireLease()) throw new InvalidOperationException("The lease cannot be acquired");
    }

    /// <summary>Acquires one additional reference unless the memory is already being torn down.</summary>
    /// <returns><c>true</c> when the caller now holds a lease to release with <see cref="IDisposable.Dispose"/>.</returns>
    public bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _leases);

    public override Span<byte> GetSpan() => _backingKind switch
    {
        BackingKind.RocksDb => _owner!.GetSpan()[.._length],
        _ => _buffer.AsSpan(0, _length),
    };

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
