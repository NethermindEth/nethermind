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
    public void Permits_follow_eth_module_concurrency(int? ethModuleConcurrentInstances, int? expected)
    {
        JsonRpcConfig config = new() { EthModuleConcurrentInstances = ethModuleConcurrentInstances };
        int permits = expected ?? Environment.ProcessorCount;

        Assert.That(
            (new EvmAdmissionGate(config).Permits, config.GetMaxConcurrentEvmExecutions()),
            Is.EqualTo((permits, permits + 1)), "the execution environment pools hold the slot above the permits too");
    }

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

    [Test]
    public async Task Priority_request_may_take_one_slot_above_the_permits_which_passes_only_to_priority()
    {
        EvmAdmissionGate gate = CreateGate(permits: 2);
        Lease first = await Admit(gate);
        Lease second = await Admit(gate);
        Task<Lease> queued = Admit(gate).AsTask();
        Task<Lease> priority = Admit(gate, priority: true).AsTask();
        Assert.That((priority.IsCompletedSuccessfully, queued.IsCompleted, gate.InFlight, gate.Queued), Is.EqualTo((true, false, 3, 1)),
            "granted at once while requests without priority hold every permit");
        Task<Lease> nextPriority = Admit(gate, priority: true).AsTask();
        Assert.That((nextPriority.IsCompleted, gate.Queued), Is.EqualTo((false, 2)), "only one slot above the permits");

        first.Dispose();
        Lease nextPriorityLease = await nextPriority.WaitAsync(TestTimeout);
        Assert.That((queued.IsCompleted, gate.InFlight, gate.Queued), Is.EqualTo((false, 3, 1)), "a slot released above the permits passes only to priority");

        (await priority).Dispose();
        Assert.That((queued.IsCompleted, gate.InFlight, gate.Queued), Is.EqualTo((false, 2, 1)), "or is freed");

        nextPriorityLease.Dispose();
        Lease queuedLease = await queued.WaitAsync(TestTimeout);
        Assert.That((gate.InFlight, gate.Queued), Is.EqualTo((2, 0)), "a slot released within the permits passes to anyone");

        queuedLease.Dispose();
        second.Dispose();
        Assert.That(gate.InFlight, Is.Zero);
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
    public async Task Priority_request_queues_past_a_full_queue_and_goes_first()
    {
        EvmAdmissionGate gate = CreateGate(queueLimit: 2);
        Lease held = await Admit(gate);
        Lease above = await TakeFreeSlot(gate, priority: true);
        List<(string Name, Task<Lease> Admission)> waiters = [("public 1", Admit(gate).AsTask()), ("public 2", Admit(gate).AsTask())];
        Task<Lease> refused = Admit(gate).AsTask();
        Assert.That(refused.IsFaulted, Is.True, "refused at once: the queue is full");
        Assert.That(async () => await refused, Throws.TypeOf<LimitExceededException>());

        waiters.Add(("priority", Admit(gate, priority: true).AsTask()));
        Assert.That((gate.Queued, gate.QueueFullRejections), Is.EqualTo((3, 1)), "a priority request queues all the same");

        held.Dispose();
        List<string> order = [await DisposeNextGrant(waiters)];
        order.AddRange(await ReleaseAndRecordGrantOrder(above, waiters));
        Assert.That(order, Is.EqualTo(new[] { "priority", "public 1", "public 2" }));
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

    [Test]
    public async Task Priority_waiters_go_first_in_arrival_order_even_ahead_of_one_that_waited_half_the_budget()
    {
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        // The slot above the permits is taken too, so the priority requests below queue.
        Lease above = await TakeFreeSlot(gate, priority: true);
        List<(string Name, Task<Lease> Admission)> waiters = [("oldest", Admit(gate).AsTask())];
        clock.Advance(TimeSpan.FromMilliseconds(BudgetMs / 2));
        waiters.Add(("heavy priority", Admit(gate, MaxWeight * BytesPerWeightUnit, priority: true).AsTask()));
        waiters.Add(("light", Admit(gate).AsTask()));
        waiters.Add(("light priority", Admit(gate, priority: true).AsTask()));

        held.Dispose();
        // Once the priority waiters are served, the slot above the permits is freed, so the others wait for the last one.
        List<string> order = [await DisposeNextGrant(waiters), await DisposeNextGrant(waiters)];
        order.AddRange(await ReleaseAndRecordGrantOrder(above, waiters));

        Assert.That(order, Is.EqualTo(new[] { "heavy priority", "light priority", "oldest", "light" }));
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
        long waitBefore = Metrics.RpcAdmissionQueueWaitMicroseconds;
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
            Assert.That(Metrics.RpcAdmissionQueuedGrants, Is.GreaterThanOrEqualTo(grantsBefore + 1), "exported");
            Assert.That(Metrics.RpcAdmissionQueueWaitMicroseconds, Is.GreaterThanOrEqualTo(waitBefore + waitedMicroseconds), "exported");
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

    private static ValueTask<Lease> Admit(
        EvmAdmissionGate gate, int paramsUtf8Length = 0, CancellationToken cancellationToken = default, TimeSpan? maxWait = null, bool priority = false) =>
        gate.AdmitAsync(paramsUtf8Length, maxWait ?? gate.Budget, priority, cancellationToken);

    // Takes a slot that should be free without waiting for it, so a gate that has none fails the test instead of hanging it
    // on a clock that never moves.
    private static ValueTask<Lease> TakeFreeSlot(EvmAdmissionGate gate, bool priority = false) => Admit(gate, maxWait: TimeSpan.Zero, priority: priority);

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

    /// <summary>A clock that moves only when told to, firing the timers that fall due.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        /// <summary>When set, <see cref="CreateTimer"/> throws it.</summary>
        public Exception? TimerFailure { get; init; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (TimerFailure is not null) throw TimerFailure;
            ManualTimer timer = new(this, callback, state);
            timer.Change(dueTime, period);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        /// <param name="elapsed">How far to move the clock.</param>
        /// <param name="fireTimers">Whether due timers fire, or stay late as on a starved timer thread.</param>
        public void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            long now = Interlocked.Add(ref _ticks, elapsed.Ticks);
            if (!fireTimers) return;

            ManualTimer[] due;
            lock (_timers)
            {
                due = [.. _timers.Where(t => t.DueTicks <= now)];
                _timers.RemoveAll(due.Contains);
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long DueTicks { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                return true;
            }

            public void Dispose()
            {
                lock (clock._timers) clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
