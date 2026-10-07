// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using NUnit.Framework;
using static Nethermind.JsonRpc.EvmAdmissionGate;

namespace Nethermind.JsonRpc.Test;

// Counts are read from the gate under test: the Metrics statics it also writes are shared by every gate in the test
// run, so only a lower bound on them can be asserted without racing the other tests.
[Parallelizable(ParallelScope.All)]
public class EvmAdmissionGateTests
{
    private const int BudgetMs = 1_000;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestCase(null, null, TestName = "Defaults to the processor count")]
    [TestCase(6, 6, TestName = "Eth module concurrency")]
    [TestCase(0, 1, TestName = "At least one")]
    public void Permits_follow_eth_module_concurrency(int? ethModuleConcurrentInstances, int? expected) =>
        Assert.That(
            new EvmAdmissionGate(new JsonRpcConfig { EthModuleConcurrentInstances = ethModuleConcurrentInstances }).Permits,
            Is.EqualTo(expected ?? Environment.ProcessorCount));

    [Test]
    public void Wait_budget_defaults_to_100_ms() =>
        Assert.That(new EvmAdmissionGate(new JsonRpcConfig()).Budget, Is.EqualTo(TimeSpan.FromMilliseconds(100)));

    [Test]
    public async Task Released_slot_passes_to_a_waiter_without_exceeding_the_permits()
    {
        EvmAdmissionGate gate = CreateGate(permits: 2);
        Lease first = await Admit(gate);
        Lease second = await Admit(gate);
        ValueTask<Lease> third = Admit(gate);
        Assert.That((third.IsCompleted, gate.InFlight, gate.Queued), Is.EqualTo((false, 2, 1)));

        first.Dispose();
        Assert.That(async () => await Admit(gate, maxWait: TimeSpan.Zero), Throws.TypeOf<LimitExceededException>(),
            "a request that may not queue never gets a slot released while others wait");
        Lease granted = await third.AsTask().WaitAsync(TestTimeout);
        Assert.That((gate.InFlight, gate.Queued), Is.EqualTo((2, 0)));

        granted.Dispose();
        second.Dispose();
        Assert.That(gate.InFlight, Is.Zero);
    }

    // After every step, at most the permits hold a slot, and a request waits only while every slot is taken.
    [Test]
    public async Task Random_admissions_and_releases_keep_the_cap_and_leave_no_waiter_behind_a_free_slot([Values(1, 2, 4)] int permits)
    {
        const int Admissions = 2_000;
        EvmAdmissionGate gate = CreateGate(permits: permits);
        Random random = new(permits);
        List<Lease> held = [];
        List<Task<Lease>> waiting = [];

        for (int step = 0, admitted = 0; admitted < Admissions || held.Count > 0; step++)
        {
            if (admitted < Admissions && (held.Count == 0 || random.Next(2) == 0))
            {
                admitted++;
                Task<Lease> admission = Admit(gate).AsTask();
                if (admission.IsCompleted) held.Add(await admission);
                else waiting.Add(admission);
            }
            else
            {
                int index = random.Next(held.Count);
                Lease released = held[index];
                held.RemoveAt(index);
                int inFlight = gate.InFlight;
                released.Dispose();
                if (gate.InFlight == inFlight)
                {
                    Task<Lease> granted = await Task.WhenAny(waiting).WaitAsync(TestTimeout);
                    waiting.Remove(granted);
                    held.Add(await granted);
                }
            }

            Assert.That((gate.InFlight, gate.Queued), Is.EqualTo((held.Count, waiting.Count)), $"step {step}: the gate counts what the test holds");
            Assert.That(held.Count, Is.LessThanOrEqualTo(permits), $"step {step}: never more than the permits");
            Assert.That(waiting.Count == 0 || held.Count == permits, Is.True, $"step {step}: a request waits only while every slot is taken");
        }

        Assert.That(waiting, Is.Empty, "every waiter is served once the others are done");
    }

    [TestCase(0, 0, BudgetMs, 0, 0, 1, TestName = "Zero budget disables queueing")]
    [TestCase(BudgetMs, 2, BudgetMs, 2, 1, 0, TestName = "Full queue")]
    [TestCase(BudgetMs, 0, 0, 0, 0, 1, TestName = "Request that may not queue")]
    [TestCase(BudgetMs, 0, BudgetMs, 3, 0, 0, TestName = "Zero queue limit leaves the queue uncapped")]
    public async Task Busy_gate_rejects_at_once_only_when_the_request_cannot_queue(
        int maxQueueWaitMs, int queueLimit, int requestMaxWaitMs, int alreadyQueued, int queueFullRejections, int notQueueableRejections)
    {
        bool rejected = queueFullRejections + notQueueableRejections > 0;
        EvmAdmissionGate gate = CreateGate(maxQueueWaitMs: maxQueueWaitMs, queueLimit: queueLimit);
        using Lease held = await Admit(gate);
        Task<Lease>[] queued = [.. Enumerable.Range(0, alreadyQueued).Select(_ => Admit(gate).AsTask())];

        Task<Lease> admission = Admit(gate, maxWait: TimeSpan.FromMilliseconds(requestMaxWaitMs)).AsTask();

        if (rejected)
        {
            Assert.That(admission.IsCompleted, Is.True, "rejected without waiting");
            Assert.That(async () => await admission, Throws.TypeOf<LimitExceededException>(), "not a wait timeout");
        }

        Assert.That(
            (gate.Queued, gate.QueueFullRejections, gate.NotQueueableRejections),
            Is.EqualTo((queued.Length + (rejected ? 0 : 1), queueFullRejections, notQueueableRejections)));
    }

    [Test]
    public async Task Waiters_are_served_in_arrival_order()
    {
        EvmAdmissionGate gate = CreateGate();
        Lease held = await Admit(gate);
        List<(string Name, Task<Lease> Admission)> waiters = [.. Enumerable.Range(0, 5).Select(i => (i.ToString(), Admit(gate).AsTask()))];

        Assert.That(await ReleaseAndRecordGrantOrder(held, waiters), Is.EqualTo(new[] { "0", "1", "2", "3", "4" }));
    }

    [Test]
    public async Task Waiter_leaving_the_middle_of_the_queue_keeps_the_others_in_arrival_order([Values] bool timesOut)
    {
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        using CancellationTokenSource cancellation = new();
        List<(string Name, Task<Lease> Admission)> waiters = [("0", Admit(gate).AsTask()), ("1", Admit(gate).AsTask())];
        Task<Lease> leaving = Admit(gate, cancellation.Token, TimeSpan.FromMilliseconds(100)).AsTask();
        waiters.AddRange([("3", Admit(gate).AsTask()), ("4", Admit(gate).AsTask())]);

        if (timesOut) clock.Advance(TimeSpan.FromMilliseconds(100));
        else cancellation.Cancel();

        Assert.That(async () => await leaving.WaitAsync(TestTimeout),
            timesOut ? Throws.TypeOf<WaitTimeoutException>() : Throws.InstanceOf<OperationCanceledException>());
        Assert.That(gate.Queued, Is.EqualTo(4), "left the queue at once");
        Assert.That(await ReleaseAndRecordGrantOrder(held, waiters), Is.EqualTo(new[] { "0", "1", "3", "4" }));
    }

    // A late timer leaves the rejection to the next release, which passes the slot on to the next waiter.
    [Test]
    public async Task Waiter_is_rejected_once_its_budget_has_elapsed([Values] bool timerFires, [Values(100, BudgetMs, 2 * BudgetMs)] int maxWaitMs)
    {
        TimeSpan maxWait = TimeSpan.FromMilliseconds(maxWaitMs);
        // A request's own wait is capped at the gate's budget.
        TimeSpan rejectedAfter = TimeSpan.FromMilliseconds(Math.Min(maxWaitMs, BudgetMs));
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        Task<Lease> waiter = Admit(gate, maxWait: maxWait).AsTask();

        clock.Advance(rejectedAfter - TimeSpan.FromMilliseconds(1), timerFires);
        Assert.That(waiter.IsCompleted, Is.False, "still waiting 1 ms before its budget ends");
        clock.Advance(TimeSpan.FromMilliseconds(1), timerFires);
        Task<Lease> next = Admit(gate, maxWait: maxWait).AsTask();
        if (!timerFires)
        {
            Assert.That(waiter.IsCompleted, Is.False);
            held.Dispose();
        }

        Assert.That(async () => await waiter.WaitAsync(TestTimeout), Throws.TypeOf<WaitTimeoutException>());
        if (timerFires)
        {
            held.Dispose();
        }

        (await next.WaitAsync(TestTimeout)).Dispose();
        Assert.That(
            (gate.InFlight, gate.Queued, gate.WaitTimeoutRejections, gate.QueuedGrants),
            Is.EqualTo((0, 0, 1L, 1L)), "only the next waiter counts as a grant");
    }

    // The waiter's timer is never due, so only the check in Release decides.
    [TestCase(BudgetMs, 100, TestName = "Well within its budget")]
    [TestCase(BudgetMs, BudgetMs - 1, TestName = "1 ms before its budget ends")]
    [TestCase(100, 99, TestName = "1 ms before a shorter wait ends")]
    public async Task Grant_after_waiting_counts_the_wait(int maxWaitMs, int waitedMs)
    {
        long grantsBefore = Metrics.RpcAdmissionQueuedGrants;
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        Assert.That((gate.QueuedGrants, gate.QueueWaitMicroseconds), Is.EqualTo((0L, 0L)), "a free slot is taken without waiting");
        Task<Lease> waiter = Admit(gate, maxWait: TimeSpan.FromMilliseconds(maxWaitMs)).AsTask();

        clock.Advance(TimeSpan.FromMilliseconds(waitedMs));
        held.Dispose();

        (await waiter.WaitAsync(TestTimeout)).Dispose();
        long waitedMicroseconds = waitedMs * 1_000L;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((gate.QueuedGrants, gate.QueueWaitMicroseconds), Is.EqualTo((1L, waitedMicroseconds)));
            // Only the grant count is checked in Metrics: a concurrent gate that adds a shorter wait can overwrite the wait sum.
            Assert.That(Metrics.RpcAdmissionQueuedGrants, Is.GreaterThanOrEqualTo(grantsBefore + 1), "exported");
        }
    }

    [Test]
    public async Task Cancelled_waiter_leaves_the_queue_without_taking_a_slot()
    {
        EvmAdmissionGate gate = CreateGate();
        Lease held = await Admit(gate);
        using CancellationTokenSource cancellation = new();
        Task<Lease> waiter = Admit(gate, cancellationToken: cancellation.Token).AsTask();

        cancellation.Cancel();

        Assert.That(async () => await waiter.WaitAsync(TestTimeout), Throws.InstanceOf<OperationCanceledException>());
        held.Dispose();
        Assert.That((gate.InFlight, gate.Queued, gate.Cancellations), Is.EqualTo((0, 0, 1)));
    }

    [Test]
    public async Task Waiter_leaves_the_queue_when_its_wait_fails_unexpectedly()
    {
        EvmAdmissionGate gate = CreateGate(new ManualClock { TimerFailure = new InvalidOperationException() });
        Lease held = await Admit(gate);

        Assert.That(async () => await Admit(gate), Throws.InvalidOperationException);
        held.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((gate.InFlight, gate.Queued), Is.EqualTo((0, 0)), "no slot is granted to a waiter nobody awaits");
            Assert.That((gate.Cancellations, gate.WaitTimeoutRejections), Is.EqualTo((0, 0)), "counted as neither a cancellation nor a timeout");
        }
    }

    [Test]
    public async Task Slot_granted_before_a_cancellation_is_observed_is_returned_not_lost()
    {
        EvmAdmissionGate gate = CreateGate();
        // Cancelling right after the grant usually lands before the waiter resumes; either way the grant must stand.
        for (int i = 0; i < 100; i++)
        {
            Lease held = await Admit(gate);
            using CancellationTokenSource cancellation = new();
            Task<Lease> waiter = Admit(gate, cancellationToken: cancellation.Token).AsTask();

            held.Dispose();
            cancellation.Cancel();

            Lease granted = await waiter.WaitAsync(TestTimeout);
            Assert.That((granted.IsGranted, gate.InFlight), Is.EqualTo((true, 1)));
            granted.Dispose();
            Assert.That(gate.InFlight, Is.Zero);
        }
    }

    private static EvmAdmissionGate CreateGate(ManualClock? clock = null, int permits = 1, int maxQueueWaitMs = BudgetMs, int queueLimit = 0) =>
        new(new JsonRpcConfig { EthModuleConcurrentInstances = permits, EvmExecutionMaxQueueWaitMs = maxQueueWaitMs, EvmExecutionQueueLimit = queueLimit }, clock ?? new ManualClock());

    private static ValueTask<Lease> Admit(EvmAdmissionGate gate, CancellationToken cancellationToken = default, TimeSpan? maxWait = null) =>
        gate.AdmitAsync(maxWait ?? gate.Budget, cancellationToken);

    // One slot: each grant is disposed before the next, so completions follow the grant order.
    private static async Task<List<string>> ReleaseAndRecordGrantOrder(Lease held, List<(string Name, Task<Lease> Admission)> waiters)
    {
        List<string> order = [];
        held.Dispose();
        while (waiters.Count > 0)
        {
            order.Add(await DisposeNextGrant(waiters));
        }

        return order;
    }

    // Waits for the next waiter to be granted, releases its slot and removes it from the list.
    private static async Task<string> DisposeNextGrant(List<(string Name, Task<Lease> Admission)> waiters)
    {
        Task<Lease> granted = await Task.WhenAny(waiters.Select(static w => w.Admission)).WaitAsync(TestTimeout);
        (string name, _) = waiters.Single(w => w.Admission == granted);
        waiters.RemoveAll(w => w.Admission == granted);
        (await granted).Dispose();
        return name;
    }
}
