// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class GasValidationResultSlotTests
{
    [Test]
    public void First_result_wins_and_is_returned()
    {
        GasValidationResultSlot slot = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.TrySetResult(new GasValidationResult(1, 2, null)), Is.True);
            Assert.That(slot.TrySetResult(new GasValidationResult(3, 4, null)), Is.False);
            Assert.That(slot.TrySetCanceled(), Is.False);
            Assert.That(slot.GetResult(), Is.EqualTo(new GasValidationResult(1, 2, null)));
        }
    }

    [Test]
    public void Canceled_slot_throws_on_get()
    {
        GasValidationResultSlot slot = new();
        slot.TrySetCanceled();

        Assert.That(slot.GetResult, Throws.InstanceOf<TaskCanceledException>());
    }

    [Test]
    public void Reset_makes_the_slot_reusable()
    {
        GasValidationResultSlot slot = new();
        slot.TrySetCanceled();
        slot.Reset();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.TrySetResult(new GasValidationResult(5, 6, null)), Is.True);
            Assert.That(slot.GetResult(), Is.EqualTo(new GasValidationResult(5, 6, null)));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public void GetResult_waits_for_a_result_published_later_from_another_thread()
    {
        GasValidationResultSlot slot = new();
        Task<GasValidationResult> waiter = Task.Factory.StartNew(slot.GetResult, TaskCreationOptions.LongRunning);
        Thread.Sleep(100); // long enough for the waiter to stop spinning and park on its event

        Assert.That(waiter.IsCompleted, Is.False);
        slot.TrySetResult(new GasValidationResult(7, 8, null));

        bool completed = waiter.Wait(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True);
            Assert.That(waiter.Result, Is.EqualTo(new GasValidationResult(7, 8, null)));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void Many_slots_handed_over_concurrently_are_all_observed_in_order()
    {
        const int count = 20_000;
        GasValidationResultSlot[] slots = new GasValidationResultSlot[count];
        for (int i = 0; i < count; i++) slots[i] = new GasValidationResultSlot();

        Task producer = Task.Run(() => Parallel.For(0, count, i => slots[i].TrySetResult(new GasValidationResult((ulong)i, 0, null))));
        ulong sum = 0;
        for (int i = 0; i < count; i++) sum += slots[i].GetResult().BlockGasUsed;
        producer.Wait();

        Assert.That(sum, Is.EqualTo((ulong)count * (count - 1) / 2));
    }
}
