// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.Self)]
public class EvmExecutionGateTests
{
    private static EvmExecutionGate Gate(int permits, int maxQueueWaitMs = 500) =>
        new(new JsonRpcConfig { EthModuleConcurrentInstances = permits, EvmExecutionMaxQueueWaitMs = maxQueueWaitMs });

    private static async Task<EvmExecutionGate.Lease> Acquire(EvmExecutionGate gate) => await gate.AcquireAsync(allowQueue: true);

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

        ValueTask<EvmExecutionGate.Lease> queued = gate.AcquireAsync(allowQueue: true);
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

        Assert.That(async () => await gate.AcquireAsync(allowQueue: true), Throws.InstanceOf<LimitExceededException>());
    }

    [Test]
    public async Task Caller_that_may_not_queue_sheds_immediately_even_with_a_budget()
    {
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 60_000);
        using EvmExecutionGate.Lease held = await Acquire(gate);

        // Would block for a minute if allowQueue were ignored.
        Assert.That(async () => await gate.AcquireAsync(allowQueue: false), Throws.InstanceOf<LimitExceededException>());
    }

    [Test]
    public async Task Waiters_are_admitted_in_arrival_order()
    {
        // The property shortest-job-first ordering gives up. A cost-ordered gate reorders these, and with no aging
        // the later-but-cheaper caller wins every release.
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000);
        EvmExecutionGate.Lease held = await Acquire(gate);

        const int waiterCount = 8;
        ValueTask<EvmExecutionGate.Lease>[] waiters = new ValueTask<EvmExecutionGate.Lease>[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            waiters[i] = gate.AcquireAsync(allowQueue: true);
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
    public async Task A_waiter_is_served_under_sustained_arrivals_rather_than_starved()
    {
        // Regression guard for cost-ordered admission without aging: a caller the ordering deprioritises must still
        // be served while new callers keep arriving, not shed at its budget for as long as the load continues.
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease> firstInLine = gate.AcquireAsync(allowQueue: true);
        Assert.That(firstInLine.IsCompleted, Is.False);

        // A stream of later arrivals, each released immediately, mimics steady light traffic.
        Task laterArrivals = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                using EvmExecutionGate.Lease lease = await gate.AcquireAsync(allowQueue: true);
            }
        });

        held.Dispose();

        // Released before draining the arrivals: with one permit, holding it here would deadlock the very traffic
        // whose pressure the test is applying.
        (await firstInLine).Dispose();

        await laterArrivals;
        Assert.Pass();
    }

    [Test]
    public async Task Queue_length_metric_tracks_waiting_callers()
    {
        long before = Interlocked.Read(ref Metrics.EvmExecutionQueueLength);
        EvmExecutionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000);
        EvmExecutionGate.Lease held = await Acquire(gate);

        ValueTask<EvmExecutionGate.Lease> queued = gate.AcquireAsync(allowQueue: true);
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

        Assert.That(async () => await gate.AcquireAsync(allowQueue: true), Throws.InstanceOf<LimitExceededException>());
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

        Assert.That(lease.Dispose, Throws.InstanceOf<SemaphoreFullException>());
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
                leases.Add(gate.AcquireAsync(allowQueue: false).Result);
            }

            Assert.That(async () => await gate.AcquireAsync(allowQueue: false), Throws.InstanceOf<LimitExceededException>());
        }
        finally
        {
            foreach (EvmExecutionGate.Lease lease in leases) lease.Dispose();
        }
    }

    [TestCase(0, Description = "a non-positive instance count still leaves one usable slot")]
    [TestCase(-5, Description = "a negative instance count still leaves one usable slot")]
    public async Task Non_positive_instance_count_still_admits_one_caller(int configured)
    {
        EvmExecutionGate gate = Gate(permits: configured, maxQueueWaitMs: 0);

        using EvmExecutionGate.Lease only = await Acquire(gate);
        Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
    }
}
