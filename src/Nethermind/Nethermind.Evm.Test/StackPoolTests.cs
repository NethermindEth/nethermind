// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class StackPoolTests
{
    [Test]
    public void Stacks_flow_between_pool_instances_through_the_thread_cache()
    {
        StackPool a = new();
        StackPool b = new();

        byte[] stack = a.RentStacks();
        Assert.That(stack.Length, Is.GreaterThanOrEqualTo(StackPool.StackLength));

        // The per-thread cache is deliberately shared across pool instances (every pool deals in
        // identically-shaped arrays), so an array returned via one instance must satisfy the next
        // rent on this thread regardless of which instance it goes through.
        b.ReturnStacks(stack);
        byte[] rerented = a.RentStacks();
        Assert.That(rerented, Is.SameAs(stack), "thread cache must serve the last returned stack LIFO");
        a.ReturnStacks(rerented);
    }

    [Test]
    public void Rent_beyond_thread_cache_still_yields_usable_stacks()
    {
        StackPool pool = new();
        byte[][] rented = new byte[24][];
        for (int i = 0; i < rented.Length; i++)
        {
            rented[i] = pool.RentStacks();
            Assert.That(rented[i].Length, Is.GreaterThanOrEqualTo(StackPool.StackLength));
        }

        // Distinctness: no array may be handed to two concurrent holders.
        for (int i = 0; i < rented.Length; i++)
            for (int j = i + 1; j < rented.Length; j++)
                Assert.That(rented[j], Is.Not.SameAs(rented[i]));

        foreach (byte[] stack in rented)
            pool.ReturnStacks(stack);
    }
}
