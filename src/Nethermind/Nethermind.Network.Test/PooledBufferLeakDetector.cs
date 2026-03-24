// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using DotNetty.Buffers;
using NUnit.Framework;

namespace Nethermind.Network.Test;

/// <summary>
/// Detects pooled buffers that were allocated but never released back to the arena.
/// On <see cref="Dispose"/>, asserts that the arena has zero active (unreleased) allocations.
///
/// Creates an isolated <see cref="PooledByteBufferAllocator"/> by default, so parallel tests
/// do not interfere with each other. Use <see cref="Allocator"/> to obtain buffers from it.
///
/// <para><b>Why cache sizes must be zero:</b>
/// DotNetty's <see cref="PooledByteBufferAllocator"/> uses thread-local caches
/// (<c>tinyCacheSize</c>, <c>smallCacheSize</c>, <c>normalCacheSize</c>).
/// When a buffer is released, it returns to the thread cache — not to the arena.
/// The arena's <c>NumActiveAllocations</c> metric only decrements when a buffer is
/// returned directly to the arena. Setting all cache sizes to <c>0</c> disables
/// the thread-local cache so that <see cref="IByteBuffer.Release"/> goes straight
/// to the arena, making the metric an accurate count of unreleased buffers.</para>
///
/// <para><b>Allocation cost:</b>
/// Each instance creates a new <see cref="PooledByteBufferAllocator"/> with its own arena.
/// This is acceptable for a small number of leak-detection tests, but if usage grows
/// significantly, consider pooling and reusing allocators across tests to avoid the overhead.</para>
///
/// <code>
/// using PooledBufferLeakDetector detector = new();
/// using var input = detector.Allocator.Buffer().AsDisposable();
/// CodeUnderTest(input);
/// // detector.Dispose() asserts no unreleased buffers remain
/// </code>
/// </summary>
public sealed class PooledBufferLeakDetector : IDisposable
{
    private readonly string _message;
    private readonly long _initialAlloc;

    /// <summary>
    /// The allocator tracked by this instance. Tests should allocate buffers from this
    /// to ensure the active-allocation count is meaningful and isolated from other tests.
    /// </summary>
    public PooledByteBufferAllocator Allocator { get; }

    /// <summary>
    /// Creates a <see cref="PooledByteBufferAllocator"/> with thread-local caches disabled,
    /// so that <see cref="IByteBuffer.Release()"/> returns buffers directly to the arena.
    /// This makes <c>NumActiveAllocations</c> an accurate count of unreleased buffers.
    /// Use this to share a single allocator across multiple <see cref="PooledBufferLeakDetector"/>
    /// instances within a test fixture, avoiding per-test arena allocation overhead.
    /// </summary>
    public static PooledByteBufferAllocator CreateAllocator() => new(
        nHeapArena: 1, nDirectArena: 0, pageSize: 4096, maxOrder: 0,
        tinyCacheSize: 0, smallCacheSize: 0, normalCacheSize: 0);

    public PooledBufferLeakDetector(PooledByteBufferAllocator? allocator = null, string? message = null)
    {
        Allocator = allocator ?? CreateAllocator();
        _message = message ?? "Pooled buffer leak: buffer was allocated from the pool but never released back";
        _initialAlloc = Allocator.Metric.HeapArenas().Sum(a => a.NumActiveAllocations);
    }

    public void Dispose()
    {
        long active = Allocator.Metric.HeapArenas().Sum(a => a.NumActiveAllocations);
        Assert.That(active, Is.EqualTo(_initialAlloc), _message);
    }
}
