// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Buffers;
using NUnit.Framework;
using RefCountingMemoryMetrics = Nethermind.Core.Buffers.Metrics.Metrics;

namespace Nethermind.Core.Test.Buffers;

public class RefCountingMemoryTests
{
    [Test]
    public void Owning_slices_to_value_length_while_wrapping_exposes_the_whole_array()
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(64);
        for (byte i = 0; i < 10; i++) rented[i] = i;

        using (RefCountingMemory owning = RefCountingMemory.Owning(rented, 10))
        {
            Assert.That(owning.GetSpan().Length, Is.EqualTo(10));
            Assert.That(owning.GetSpan().ToArray(), Is.EqualTo(rented[..10]));
        }

        byte[] array = [1, 2, 3];
        using RefCountingMemory wrapping = RefCountingMemory.Wrapping(array);
        Assert.That(wrapping.GetSpan().ToArray(), Is.EqualTo(array));
    }

    [Test]
    public void WrappingOrNull_maps_a_null_array_to_null()
    {
        Assert.That(RefCountingMemory.WrappingOrNull(null), Is.Null);
        using RefCountingMemory? memory = RefCountingMemory.WrappingOrNull([1]);
        Assert.That(memory, Is.Not.Null);
    }

    [Test]
    public void Cleanup_runs_only_on_the_last_release_and_over_disposing_throws()
    {
        RefCountingMemory mem = RefCountingMemory.Wrapping([1, 2, 3]);
        mem.AcquireLease();

        ((IDisposable)mem).Dispose();
        Assert.That(mem.GetSpan().Length, Is.EqualTo(3), "still leased once");

        ((IDisposable)mem).Dispose();
        Assert.Throws<ObjectDisposedException>(() => ((IDisposable)mem).Dispose());
    }

    [Test]
    public void A_fully_released_memory_cannot_be_leased_again()
    {
        RefCountingMemory mem = RefCountingMemory.Wrapping([1]);
        ((IDisposable)mem).Dispose();
        Assert.Throws<InvalidOperationException>(mem.AcquireLease);
    }

    [Test]
    public void Pooled_provider_rents_memory_of_the_requested_length()
    {
        using RefCountingMemory mem = PooledRefCountingMemoryProvider.Instance.Rent(20);
        Assert.That(mem.GetSpan().Length, Is.EqualTo(20));
    }

    [Test]
    public void Metrics_track_active_pooled_capacity_and_non_pooled_count_until_final_release()
    {
        long initialPooledCount = RefCountingMemoryMetrics.ActivePooledRefCountingMemoryCount;
        long initialPooledCapacity = RefCountingMemoryMetrics.ActivePooledRefCountingMemoryCapacity;
        long initialNonPooledCount = RefCountingMemoryMetrics.ActiveNonPooledRefCountingMemoryCount;
        byte[] rented = ArrayPool<byte>.Shared.Rent(65);
        RefCountingMemory? owning = null;
        RefCountingMemory? wrapping = null;

        try
        {
            owning = RefCountingMemory.Owning(rented, 65);
            wrapping = RefCountingMemory.Wrapping([1, 2, 3]);
            AssertMetrics(initialPooledCount + 1, initialPooledCapacity + rented.Length, initialNonPooledCount + 1);

            owning.AcquireLease();
            ((IDisposable)owning).Dispose();
            AssertMetrics(initialPooledCount + 1, initialPooledCapacity + rented.Length, initialNonPooledCount + 1);

            ((IDisposable)wrapping).Dispose();
            wrapping = null;
            ((IDisposable)owning).Dispose();
            owning = null;
            AssertMetrics(initialPooledCount, initialPooledCapacity, initialNonPooledCount);
        }
        finally
        {
            ((IDisposable?)wrapping)?.Dispose();
            ((IDisposable?)owning)?.Dispose();
        }
    }

    [Test]
    public void OwningRocksDb_adopts_memory_until_the_final_lease_and_tracks_exclusive_metrics()
    {
        long initialPooledCount = RefCountingMemoryMetrics.ActivePooledRefCountingMemoryCount;
        long initialPooledCapacity = RefCountingMemoryMetrics.ActivePooledRefCountingMemoryCapacity;
        long initialNonPooledCount = RefCountingMemoryMetrics.ActiveNonPooledRefCountingMemoryCount;
        long initialRocksDbCount = RefCountingMemoryMetrics.ActiveRocksDbRefCountingMemoryCount;
        long initialRocksDbCapacity = RefCountingMemoryMetrics.ActiveRocksDbRefCountingMemoryCapacity;
        byte[] array = [1, 2, 3];
        TrackingMemoryManager owner = new(array);
        RefCountingMemory? memory = null;

        try
        {
            memory = RefCountingMemory.OwningRocksDb(owner);
            array[1] = 4;
            memory.AcquireLease();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(memory.GetSpan().ToArray(), Is.EqualTo(new byte[] { 1, 4, 3 }), "owned memory is not copied");
                Assert.That(owner.IsDisposed, Is.False, "owner before release");
                AssertMetrics(initialPooledCount, initialPooledCapacity, initialNonPooledCount);
                Assert.That(RefCountingMemoryMetrics.ActiveRocksDbRefCountingMemoryCount, Is.EqualTo(initialRocksDbCount + 1), "RocksDB count");
                Assert.That(RefCountingMemoryMetrics.ActiveRocksDbRefCountingMemoryCapacity, Is.EqualTo(initialRocksDbCapacity + array.Length), "RocksDB capacity");
            }

            ((IDisposable)memory).Dispose();
            Assert.That(owner.IsDisposed, Is.False, "owner with one remaining lease");

            ((IDisposable)memory).Dispose();
            memory = null;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(owner.IsDisposed, Is.True, "owner after final release");
                AssertMetrics(initialPooledCount, initialPooledCapacity, initialNonPooledCount);
                Assert.That(RefCountingMemoryMetrics.ActiveRocksDbRefCountingMemoryCount, Is.EqualTo(initialRocksDbCount), "RocksDB count after release");
                Assert.That(RefCountingMemoryMetrics.ActiveRocksDbRefCountingMemoryCapacity, Is.EqualTo(initialRocksDbCapacity), "RocksDB capacity after release");
            }
        }
        finally
        {
            ((IDisposable?)memory)?.Dispose();
        }
    }

    [Test]
    public void Shrink_narrows_the_value_to_what_a_producer_wrote()
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(64);
        for (byte i = 0; i < 10; i++) rented[i] = i;

        using RefCountingMemory mem = RefCountingMemory.Owning(rented, 10);
        mem.Shrink(4);
        Assert.That(mem.GetSpan().ToArray(), Is.EqualTo(rented[..4]));

        mem.Shrink(0);
        Assert.That(mem.GetSpan().IsEmpty);
    }

    private sealed class TrackingMemoryManager(byte[] array) : MemoryManager<byte>
    {
        public bool IsDisposed { get; private set; }

        public override Span<byte> GetSpan() => array;

        public override MemoryHandle Pin(int elementIndex = 0) => default;

        public override void Unpin() { }

        protected override void Dispose(bool disposing) => IsDisposed = true;
    }

    private static void AssertMetrics(long pooledCount, long pooledCapacity, long nonPooledCount)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RefCountingMemoryMetrics.ActivePooledRefCountingMemoryCount, Is.EqualTo(pooledCount), "pooled count");
            Assert.That(RefCountingMemoryMetrics.ActivePooledRefCountingMemoryCapacity, Is.EqualTo(pooledCapacity), "pooled capacity");
            Assert.That(RefCountingMemoryMetrics.ActiveNonPooledRefCountingMemoryCount, Is.EqualTo(nonPooledCount), "non-pooled count");
        }
    }
}
