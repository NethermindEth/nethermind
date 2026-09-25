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
        Assert.That(async () => await gate.AdmitAsync(0, allowQueue: false, CancellationToken.None), Throws.InstanceOf<LimitExceededException>(),
            "a request that may not queue never gets a slot released while others wait");
        Lease granted = await third.AsTask().WaitAsync(TestTimeout);
        Assert.That((gate.InFlight, gate.Queued), Is.EqualTo((2, 0)));

        granted.Dispose();
        second.Dispose();
        Assert.That(gate.InFlight, Is.Zero);
    }

    [TestCase(0, 0, true, 0, true, TestName = "Zero budget disables queueing")]
    [TestCase(BudgetMs, 2, true, 2, true, TestName = "Full queue")]
    [TestCase(BudgetMs, 0, false, 0, true, TestName = "Request that may not queue")]
    [TestCase(BudgetMs, 0, true, 3, false, TestName = "Zero queue limit leaves the queue uncapped")]
    [NonParallelizable]
    public async Task Busy_gate_rejects_at_once_only_when_the_request_cannot_queue(
        int maxQueueWaitMs, int queueLimit, bool allowQueue, int alreadyQueued, bool rejected)
    {
        long rejectionsBefore = Metrics.RpcAdmissionImmediateRejections;
        EvmAdmissionGate gate = CreateGate(maxQueueWaitMs: maxQueueWaitMs, queueLimit: queueLimit);
        using Lease held = await Admit(gate);
        Task<Lease>[] queued = [.. Enumerable.Range(0, alreadyQueued).Select(_ => Admit(gate).AsTask())];

        Task<Lease> admission = gate.AdmitAsync(0, allowQueue, CancellationToken.None).AsTask();

        if (rejected)
        {
            Assert.That(admission.IsCompleted, Is.True, "rejected without waiting");
            Assert.That(async () => await admission, Throws.InstanceOf<LimitExceededException>());
        }

        Assert.That((gate.Queued, Metrics.RpcAdmissionImmediateRejections - rejectionsBefore),
            Is.EqualTo((queued.Length + (rejected ? 0 : 1), rejected ? 1 : 0)));
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

        // One slot: each grant is disposed before the next, so completions follow the grant order.
        List<string> order = [];
        held.Dispose();
        while (waiters.Count > 0)
        {
            Task<Lease> granted = await Task.WhenAny(waiters.Select(static w => w.Admission)).WaitAsync(TestTimeout);
            (string name, _) = waiters.Single(w => w.Admission == granted);
            waiters.RemoveAll(w => w.Admission == granted);
            order.Add(name);
            (await granted).Dispose();
        }

        Assert.That(order, Is.EqualTo(new[] { "light 1", "light 2", "heavy", "light 3" }));
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

    [TestCase(true, TestName = "Timer fires at the budget")]
    [TestCase(false, TestName = "Late timer: rejected when a slot frees, which passes to the next waiter")]
    [NonParallelizable]
    public async Task Waiter_is_rejected_once_its_budget_has_elapsed(bool timerFires)
    {
        long rejectionsBefore = Metrics.RpcAdmissionWaitTimeoutRejections;
        ManualClock clock = new();
        EvmAdmissionGate gate = CreateGate(clock);
        Lease held = await Admit(gate);
        Task<Lease> waiter = Admit(gate).AsTask();

        clock.Advance(TimeSpan.FromMilliseconds(BudgetMs), timerFires);
        Task<Lease> next = Admit(gate).AsTask();
        if (!timerFires)
        {
            Assert.That(waiter.IsCompleted, Is.False);
            held.Dispose();
        }

        Assert.That(async () => await waiter.WaitAsync(TestTimeout), Throws.InstanceOf<LimitExceededException>());
        if (timerFires)
        {
            held.Dispose();
        }

        (await next.WaitAsync(TestTimeout)).Dispose();
        Assert.That((gate.InFlight, gate.Queued, Metrics.RpcAdmissionWaitTimeoutRejections - rejectionsBefore), Is.EqualTo((0, 0, 1)));
    }

    [Test]
    [NonParallelizable]
    public async Task Cancelled_waiter_leaves_the_queue_without_taking_a_slot()
    {
        long cancellationsBefore = Metrics.RpcAdmissionCancellations;
        EvmAdmissionGate gate = CreateGate();
        Lease held = await Admit(gate);
        using CancellationTokenSource cancellation = new();
        Task<Lease> waiter = Admit(gate, cancellationToken: cancellation.Token).AsTask();

        cancellation.Cancel();

        Assert.That(async () => await waiter.WaitAsync(TestTimeout), Throws.InstanceOf<OperationCanceledException>());
        held.Dispose();
        Assert.That((gate.InFlight, gate.Queued, Metrics.RpcAdmissionCancellations - cancellationsBefore), Is.EqualTo((0, 0, 1)));
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

    private static ValueTask<Lease> Admit(EvmAdmissionGate gate, int paramsUtf8Length = 0, CancellationToken cancellationToken = default) =>
        gate.AdmitAsync(paramsUtf8Length, allowQueue: true, cancellationToken);

    /// <summary>A clock that moves only when told to, firing the timers that fall due.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
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
