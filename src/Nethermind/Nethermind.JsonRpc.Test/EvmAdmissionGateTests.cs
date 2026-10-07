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

    [TestCase(0, ExpectedResult = 1)]
    [TestCase(BytesPerWeightUnit - 1, ExpectedResult = 1)]
    [TestCase(BytesPerWeightUnit, ExpectedResult = 2)]
    [TestCase(int.MaxValue, ExpectedResult = MaxWeight)]
    public int Weight_grows_with_params_size(int paramsUtf8Length) => Weigh(paramsUtf8Length);

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
    public async Task Smaller_requests_go_first_but_never_ahead_of_one_that_arrived_its_size_penalty_earlier()
    {
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        // Weight 4 delays its turn by 3/14 of the budget, about 214 ms; all grants below happen before anyone has waited half the budget.
        List<(string Name, Task<Lease> Admission)> waiters =
        [
            ("heavy", Admit(gate, 3 * BytesPerWeightUnit).AsTask()),
            ("light 1", Admit(gate).AsTask()),
        ];
        clock.Advance(TimeSpan.FromMilliseconds(3 * BudgetMs / 14));
        waiters.Add(("light 2", Admit(gate).AsTask()));
        clock.Advance(TimeSpan.FromMilliseconds(2));
        waiters.Add(("light 3", Admit(gate).AsTask()));

        Assert.That(await ReleaseAndRecordGrantOrder(held, waiters), Is.EqualTo(new[] { "light 1", "light 2", "heavy", "light 3" }));
    }

    [Test]
    public async Task Equal_keys_are_served_in_enqueue_order()
    {
        EvmAdmissionGate gate = CreateGate();
        Lease held = await Admit(gate);
        // The clock never moves, so all keys are equal. Two waiters would be too few for the heap to reorder them.
        List<(string Name, Task<Lease> Admission)> waiters = [.. Enumerable.Range(0, 5).Select(i => (i.ToString(), Admit(gate).AsTask()))];

        Assert.That(await ReleaseAndRecordGrantOrder(held, waiters), Is.EqualTo(new[] { "0", "1", "2", "3", "4" }));
    }

    [TestCase(BudgetMs / 2 - 1, "light", TestName = "Just before half the budget: size order")]
    [TestCase(BudgetMs / 2, "heavy", TestName = "At half the budget: oldest first")]
    public async Task Oldest_waiter_goes_first_exactly_at_half_the_budget(int releaseAtMs, string expectedFirst)
    {
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        // By size alone, the heavy request comes after one that arrived up to almost half the budget later.
        List<(string Name, Task<Lease> Admission)> waiters = [("heavy", Admit(gate, MaxWeight * BytesPerWeightUnit).AsTask())];
        clock.Advance(TimeSpan.FromMilliseconds(100));
        waiters.Add(("light", Admit(gate).AsTask()));
        clock.Advance(TimeSpan.FromMilliseconds(releaseAtMs - 100));

        Assert.That((await ReleaseAndRecordGrantOrder(held, waiters))[0], Is.EqualTo(expectedFirst));
    }

    // Released at 250 ms: past half of the heavy waiter's 400 ms, but before it has waited half the budget.
    [TestCase(2 * BudgetMs / 10 - 1, "light", TestName = "Arrived within half of the shorter wait: size order")]
    [TestCase(2 * BudgetMs / 10, "heavy", TestName = "Arrived half the shorter wait later: heavy first")]
    public async Task Size_penalty_is_capped_at_half_of_what_the_waiter_may_wait(int lightArrivesAtMs, string expectedFirst)
    {
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        List<(string Name, Task<Lease> Admission)> waiters =
            [("heavy", Admit(gate, MaxWeight * BytesPerWeightUnit, maxWait: TimeSpan.FromMilliseconds(4 * BudgetMs / 10)).AsTask())];
        clock.Advance(TimeSpan.FromMilliseconds(lightArrivesAtMs));
        waiters.Add(("light", Admit(gate).AsTask()));
        clock.Advance(TimeSpan.FromMilliseconds(BudgetMs / 4 - lightArrivesAtMs));

        Assert.That((await ReleaseAndRecordGrantOrder(held, waiters))[0], Is.EqualTo(expectedFirst));
    }

    [Test]
    public async Task Sustained_lighter_traffic_cannot_starve_a_heavy_waiter()
    {
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease slot = await Admit(gate);
        // One grant and one new light waiter per 100 ms behind a backlog of seven: every light request waits 700 ms, within the
        // budget but always ahead of the heavy request on size alone.
        List<Task<Lease>> waiters = [.. Enumerable.Range(0, 7).Select(_ => Admit(gate).AsTask())];
        Task<Lease> heavy = Admit(gate, MaxWeight * BytesPerWeightUnit).AsTask();
        waiters.Add(heavy);

        for (int waitedMs = 100; waitedMs < BudgetMs && !heavy.IsCompleted; waitedMs += 100)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            waiters.Add(Admit(gate).AsTask());
            slot.Dispose();
            Task<Lease> granted = await Task.WhenAny(waiters).WaitAsync(TestTimeout);
            waiters.Remove(granted);
            slot = await granted;
        }

        Assert.That(heavy.IsCompletedSuccessfully, Is.True, "granted within its budget");
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

    private static ValueTask<Lease> Admit(EvmAdmissionGate gate, int paramsUtf8Length = 0, CancellationToken cancellationToken = default, TimeSpan? maxWait = null) =>
        gate.AdmitAsync(paramsUtf8Length, maxWait ?? gate.Budget, cancellationToken);

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
