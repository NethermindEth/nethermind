// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

// The queue-length assertions read a process-global metric that EvmExecutionGateServiceTests also moves.
[NonParallelizable]
public class EvmExecutionGateTests
{
    /// <summary>A tick source the test advances by hand, so the aging window does not depend on wall-clock timing.</summary>
    private sealed class TestClock
    {
        private long _ticks;

        internal long Now() => _ticks;

        internal void Advance(long ticks) => _ticks += ticks;
    }

    private static EvmExecutionGate Gate(int permits, int maxQueueWaitMs = 500, TestClock? clock = null) =>
        new(new JsonRpcConfig { EthModuleConcurrentInstances = permits, EvmExecutionMaxQueueWaitMs = maxQueueWaitMs },
            clock is null ? null : clock.Now);

    private static long Quantum(int maxQueueWaitMs) =>
        (long)(TimeSpan.FromMilliseconds(maxQueueWaitMs).TotalSeconds * Stopwatch.Frequency) / EvmExecutionGate.QuantumsPerBudget;

    private static async Task<EvmExecutionGate.Lease> Acquire(EvmExecutionGate gate, int weight = 1) =>
        await gate.AcquireAsync(weight, allowQueue: true);

    [Test]
    public async Task Admits_exactly_the_configured_number_of_permits()
    {
        EvmExecutionGate gate = Gate(permits: 2, maxQueueWaitMs: 0);

        using EvmExecutionGate.Lease first = await Acquire(gate);
        using EvmExecutionGate.Lease second = await Acquire(gate);

        Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
    }

    [Test]
    public async Task Releasing_a_permit_admits_the_next_caller()
    {
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 0);

        using (EvmExecutionGate.Lease held = await Acquire(gate))
        {
            Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
        }

        using EvmExecutionGate.Lease afterRelease = await Acquire(gate);
        Assert.Pass();
    }

    [Test]
    public async Task Queued_request_is_admitted_when_a_permit_frees_within_the_budget()
    {
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 5_000);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease> queued = gate.AcquireAsync(1, allowQueue: true);
        Assert.That(queued.IsCompleted, Is.False, "the gate is saturated, so this caller must wait");

        held.Dispose();

        using EvmExecutionGate.Lease admitted = await queued;
        Assert.Pass();
    }

    [Test]
    public async Task Saturated_gate_sheds_once_the_wait_budget_expires()
    {
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30);
        using EvmExecutionGate.Lease held = await Acquire(gate);

        Assert.That(async () => await gate.AcquireAsync(1, allowQueue: true), Throws.InstanceOf<LimitExceededException>());
    }

    [Test]
    public async Task Caller_that_may_not_queue_sheds_immediately_even_with_a_budget()
    {
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 60_000);
        using EvmExecutionGate.Lease held = await Acquire(gate);

        // Refused at the call, not after the minute-long budget - see ShedsOnArrival.
        Assert.That(ShedsOnArrival(gate, allowQueue: false), Is.True);
    }

    [Test]
    public async Task Waiters_of_equal_weight_are_admitted_in_arrival_order()
    {
        // Weight decides the ordering only between different cost classes; within one class the deadline reduces
        // to arrival time, so equal-weight callers keep strict FIFO and none can be overtaken.
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000);
        EvmExecutionGate.Lease held = await Acquire(gate);

        const int waiterCount = 8;
        ValueTask<EvmExecutionGate.Lease>[] waiters = new ValueTask<EvmExecutionGate.Lease>[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            waiters[i] = gate.AcquireAsync(1, allowQueue: true);
            // Each waiter must be queued before the next one arrives, or arrival order is not defined.
            Assert.That(waiters[i].IsCompleted, Is.False);
        }

        List<int> admissionOrder = [];
        Task[] observers = new Task[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            int index = i;
            ValueTask<EvmExecutionGate.Lease> waiter = waiters[i];
            observers[i] = Task.Run(async () =>
            {
                using EvmExecutionGate.Lease lease = await waiter;
                lock (admissionOrder) admissionOrder.Add(index);
            });
        }

        held.Dispose();
        await Task.WhenAll(observers);

        Assert.That(admissionOrder, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Test]
    public async Task Lighter_callers_are_admitted_ahead_of_a_heavier_one_that_arrived_first()
    {
        // The reason for ordering by cost at all: a small eth_call must not sit behind large simulations, which is
        // what drives the mean response time an operator actually perceives.
        TestClock clock = new();
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000, clock);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease> heavy = gate.AcquireAsync(EvmExecutionGate.MaxWeight, allowQueue: true);
        ValueTask<EvmExecutionGate.Lease> light = gate.AcquireAsync(1, allowQueue: true);

        held.Dispose();

        // The light caller arrived second but carries less slack, so it holds the only permit.
        using (await light)
        {
            Assert.That(heavy.IsCompleted, Is.False, "the heavier caller must still be waiting");
        }

        (await heavy).Dispose();
    }

    [Test]
    public async Task A_heavy_waiter_is_not_overtaken_once_its_slack_has_elapsed()
    {
        // The aging property. Unaged cost ordering lets every later light caller overtake forever, so a heavy
        // request sheds at its budget for as long as light load continues; anchoring the deadline to arrival
        // bounds the overtaking at (weight - 1) quanta.
        const int budgetMs = 30_000;
        TestClock clock = new();
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: budgetMs, clock);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease> heavy = gate.AcquireAsync(EvmExecutionGate.MaxWeight, allowQueue: true);

        // Past the heavy caller's slack window, so any later light caller now has the later deadline.
        clock.Advance(Quantum(budgetMs) * EvmExecutionGate.MaxWeight);
        ValueTask<EvmExecutionGate.Lease> lateLight = gate.AcquireAsync(1, allowQueue: true);

        held.Dispose();

        using (await heavy)
        {
            Assert.That(lateLight.IsCompleted, Is.False, "the aged-in heavy caller must go first");
        }

        (await lateLight).Dispose();
    }

    [Test]
    public async Task A_heavy_waiter_is_served_after_the_light_callers_that_legitimately_overtook_it()
    {
        // End-to-end guard on the same property. Every waiter is queued on the test thread before anything is
        // released, so the overtaking actually happens rather than depending on a background task winning a race.
        const int budgetMs = 30_000;
        TestClock clock = new();
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: budgetMs, clock);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease> heavy = gate.AcquireAsync(EvmExecutionGate.MaxWeight, allowQueue: true);
        Assert.That(heavy.IsCompleted, Is.False);

        // Inside the heavy caller's slack window, so these are entitled to overtake it.
        const int overtakerCount = 3;
        ValueTask<EvmExecutionGate.Lease>[] overtakers = new ValueTask<EvmExecutionGate.Lease>[overtakerCount];
        for (int i = 0; i < overtakerCount; i++)
        {
            overtakers[i] = gate.AcquireAsync(1, allowQueue: true);
        }

        // Past the window: these must not overtake, however many of them arrive.
        clock.Advance(Quantum(budgetMs) * EvmExecutionGate.MaxWeight);
        ValueTask<EvmExecutionGate.Lease>[] lateArrivals = new ValueTask<EvmExecutionGate.Lease>[overtakerCount];
        for (int i = 0; i < overtakerCount; i++)
        {
            lateArrivals[i] = gate.AcquireAsync(1, allowQueue: true);
        }

        held.Dispose();

        for (int i = 0; i < overtakerCount; i++)
        {
            (await overtakers[i]).Dispose();
        }

        // The heavy caller goes before every late arrival, so sustained light load cannot keep pushing it back.
        Assert.That(lateArrivals[0].IsCompleted, Is.False, "the aged-in heavy caller must precede later arrivals");
        (await heavy).Dispose();

        for (int i = 0; i < overtakerCount; i++)
        {
            (await lateArrivals[i]).Dispose();
        }
    }

    [Test]
    public async Task Arrivals_beyond_the_queue_depth_cap_are_shed_rather_than_queued()
    {
        // The cap bounds queue depth and refuses past it on arrival. It makes no promise about the wait - see the
        // remark on EvmExecutionGate, which is explicit that the ordering is not a liveness guarantee.
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease>[] queued =
            new ValueTask<EvmExecutionGate.Lease>[EvmExecutionGate.MaxQueueDepthPerPermit];
        for (int i = 0; i < queued.Length; i++)
        {
            queued[i] = gate.AcquireAsync(1, allowQueue: true);
            Assert.That(queued[i].IsCompleted, Is.False);
        }

        // Asserted on the synchronous throw, not by awaiting: a caller that queues instead raises the *same*
        // exception once the budget expires, so awaiting cannot tell "refused on arrival" from "waited 30s".
        Assert.That(ShedsOnArrival(gate), Is.True, "over the cap the gate must refuse on arrival");
        Assert.That(gate.QueuedCount, Is.EqualTo(queued.Length), "the shed caller must not have been queued");

        // Drained rather than abandoned: an un-awaited waiter faults a whole budget later and moves the shared
        // queue-length gauge in the middle of some other test.
        held.Dispose();
        for (int i = 0; i < queued.Length; i++)
        {
            (await queued[i]).Dispose();
        }
    }

    /// <summary>Whether the gate refuses without queueing, i.e. <see cref="EvmExecutionGate.AcquireAsync"/> throws
    /// at the call itself rather than from the returned task after the wait budget.</summary>
    private static bool ShedsOnArrival(EvmExecutionGate gate, bool allowQueue = true)
    {
        ValueTask<EvmExecutionGate.Lease> pending;
        try
        {
            pending = gate.AcquireAsync(1, allowQueue);
        }
        catch (LimitExceededException)
        {
            return true;
        }

        // Not shed - but "did not throw" alone would also cover a free permit, which would mean the caller never
        // tested the saturated gate it meant to. Distinguish the two rather than reporting both as "queued".
        if (pending.IsCompletedSuccessfully)
        {
            pending.Result.Dispose();
            Assert.Fail("a permit was free, so the gate was not saturated and the shed path was never exercised");
        }

        // Genuinely queued. Observe the task so a later timeout is not an unobserved fault.
        _ = pending.AsTask().ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        return false;
    }

    [Test]
    public async Task The_gate_still_admits_after_heavy_concurrent_expiry()
    {
        // Exercises the paths that maintain the live count concurrently - enqueue under the lock, expiry from the
        // timer thread, release from the holder - and checks the count settles and the gate is still usable.
        //
        // It does NOT pin the lost-update race the Interlocked increment exists for: that window is a couple of
        // instructions between the read and write of a non-atomic ++, and it does not reproduce here (measured:
        // 3/3 passes against the non-atomic version). That fix rests on the memory model, not on this test. What
        // this does catch is a wholesale accounting mistake, such as a decrement path that stops running.
        EvmExecutionGate gate = Gate(permits: 2, maxQueueWaitMs: 40);
        EvmExecutionGate.Lease first = await Acquire(gate);
        EvmExecutionGate.Lease second = await Acquire(gate);

        const int threads = 8;
        const int perThread = 40;
        Task[] workers = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = Task.Run(async () =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    try
                    {
                        (await gate.AcquireAsync(1, allowQueue: true)).Dispose();
                    }
                    catch (LimitExceededException)
                    {
                        // Expected: the gate is saturated for the whole run.
                    }
                }
            });
        }

        await Task.WhenAll(workers);
        first.Dispose();
        second.Dispose();

        // Polled, not sampled: Settle completes the waiter before it decrements, and the continuation resumes
        // asynchronously, so a worker can finish while the settling thread is still a statement short. A bare
        // assertion here would flake as the very accounting bug it exists to detect.
        Assert.That(() => gate.QueuedCount, Is.Zero.After(2000, 50),
            "a lost decrement never recovers, and the first one disables the free-permit fast path for good");

        // The fast path is the thing the drift destroys, so check it still works rather than only the counter.
        using EvmExecutionGate.Lease afterwards = await Acquire(gate);
        Assert.Pass();
    }

    [Test]
    public async Task Equal_deadlines_keep_arrival_order()
    {
        // PriorityQueue is not a stable heap, so equal deadlines need the sequence tiebreaker. A frozen clock makes
        // every deadline identical, which is the case a real clock only hits when two arrivals share a tick.
        TestClock clock = new();
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000, clock);
        EvmExecutionGate.Lease held = await Acquire(gate);

        const int waiterCount = 8;
        ValueTask<EvmExecutionGate.Lease>[] waiters = new ValueTask<EvmExecutionGate.Lease>[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            waiters[i] = gate.AcquireAsync(1, allowQueue: true);
        }

        // Admission resumes asynchronously by design (see Waiter), so order is observed rather than polled.
        List<int> admissionOrder = [];
        Task[] observers = new Task[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            int index = i;
            ValueTask<EvmExecutionGate.Lease> waiter = waiters[i];
            observers[i] = Task.Run(async () =>
            {
                using EvmExecutionGate.Lease lease = await waiter;
                lock (admissionOrder) admissionOrder.Add(index);
            });
        }

        held.Dispose();
        await Task.WhenAll(observers);

        Assert.That(admissionOrder, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
    }

    [Test]
    public async Task Queue_length_metric_tracks_waiting_callers()
    {
        long before = Interlocked.Read(ref Metrics.EvmExecutionQueueLength);
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease> queued = gate.AcquireAsync(1, allowQueue: true);
        Assert.That(Interlocked.Read(ref Metrics.EvmExecutionQueueLength), Is.EqualTo(before + 1));

        held.Dispose();
        using EvmExecutionGate.Lease admitted = await queued;

        Assert.That(Interlocked.Read(ref Metrics.EvmExecutionQueueLength), Is.EqualTo(before));
    }

    [Test]
    public async Task Shed_caller_is_removed_from_the_queue_length()
    {
        long before = Interlocked.Read(ref Metrics.EvmExecutionQueueLength);
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 20);
        using EvmExecutionGate.Lease held = await Acquire(gate);

        Assert.That(async () => await gate.AcquireAsync(1, allowQueue: true), Throws.InstanceOf<LimitExceededException>());
        Assert.That(Interlocked.Read(ref Metrics.EvmExecutionQueueLength), Is.EqualTo(before));
    }

    [Test]
    public async Task Releasing_a_lease_twice_throws_rather_than_inflating_the_permit_count()
    {
        // A leaked extra permit would silently widen EVM concurrency for the rest of the process, so the maxCount
        // constructor is load-bearing: the bug must surface loudly instead.
        EvmExecutionGate gate = Gate(permits: 1);
        EvmExecutionGate.Lease lease = await Acquire(gate);
        lease.Dispose();

        Assert.That(lease.Dispose, Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Permit_count_falls_back_to_the_processor_count()
    {
        EvmExecutionGate gate = new(new JsonRpcConfig { EthModuleConcurrentInstances = null, EvmExecutionMaxQueueWaitMs = 0 });

        List<EvmExecutionGate.Lease> leases = [];
        try
        {
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                leases.Add(gate.AcquireAsync(1, allowQueue: false).Result);
            }

            Assert.That(async () => await gate.AcquireAsync(1, allowQueue: false), Throws.InstanceOf<LimitExceededException>());
        }
        finally
        {
            foreach (EvmExecutionGate.Lease lease in leases) lease.Dispose();
        }
    }

    [TestCase(0, 1, Description = "no params is the lightest class")]
    [TestCase(EvmExecutionGate.BytesPerWeightUnit - 1, 1, Description = "just under one unit stays lightest")]
    [TestCase(EvmExecutionGate.BytesPerWeightUnit, 2, Description = "one full unit moves up a class")]
    [TestCase(EvmExecutionGate.BytesPerWeightUnit * 64, EvmExecutionGate.MaxWeight, Description = "clamped at the heaviest class")]
    public void Weight_follows_the_raw_params_length(int paramsByteLength, int expected) =>
        Assert.That(EvmExecutionGate.Weigh(paramsByteLength), Is.EqualTo(expected));

    [TestCase(0, Description = "a non-positive instance count still leaves one usable slot")]
    [TestCase(-5, Description = "a negative instance count still leaves one usable slot")]
    public async Task Non_positive_instance_count_still_admits_one_caller(int configured)
    {
        EvmExecutionGate gate = Gate(permits: configured, maxQueueWaitMs: 0);

        using EvmExecutionGate.Lease only = await Acquire(gate);
        Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
    }
}
