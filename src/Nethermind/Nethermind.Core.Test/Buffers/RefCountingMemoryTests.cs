// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Buffers;
using NUnit.Framework;

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
        Assert.That(RefCountingMemory.WrappingOrNull([1]), Is.Not.Null);
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
}
