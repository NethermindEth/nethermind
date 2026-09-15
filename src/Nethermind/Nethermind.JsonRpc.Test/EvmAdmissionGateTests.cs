// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using NUnit.Framework;
using static Nethermind.JsonRpc.EvmAdmissionGate;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class EvmAdmissionGateTests
{
    /// <summary>A tick source the test advances by hand, so the ordering window does not depend on wall-clock timing.</summary>
    private sealed class TestClock
    {
        private long _ticks;

        internal long Now() => _ticks;

        internal void Advance(long ticks) => _ticks += ticks;
    }

    /// <summary>Runs posted continuations only when pumped, so a grant can be settled without letting the waiter that
    /// owns it resume.</summary>
    private sealed class ManualSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();

        internal int Pending
        {
            get { lock (_posted) return _posted.Count; }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_posted) _posted.Enqueue((d, state));
        }

        internal void RunPending()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_posted)
                {
                    if (_posted.Count == 0) return;

                    next = _posted.Dequeue();
                }

                next.Callback(next.State);
            }
        }
    }

    private static EvmAdmissionGate Gate(int permits, int maxQueueWaitMs = 500, TestClock? clock = null) =>
        new(new JsonRpcConfig { EthModuleConcurrentInstances = permits, EvmExecutionMaxQueueWaitMs = maxQueueWaitMs },
            clock is null ? null : clock.Now);

    private static long Quantum(int maxQueueWaitMs) =>
        (long)(TimeSpan.FromMilliseconds(maxQueueWaitMs).TotalSeconds * Stopwatch.Frequency) / QuantumsPerBudget;

    /// <summary>The raw params length that <see cref="Weigh"/> maps onto <paramref name="weight"/>.</summary>
    private static int ParamsFor(int weight) => (weight - 1) * BytesPerWeightUnit;

    private static ValueTask<Lease> Acquire(EvmAdmissionGate gate, int weight = 1, CancellationToken cancellationToken = default) =>
        gate.AdmitAsync(ParamsFor(weight), cancellationToken);

    /// <summary>Acquires as an authenticated caller, which an anonymous one may never overtake.</summary>
    private static ValueTask<Lease> AcquireTrusted(EvmAdmissionGate gate) =>
        gate.AdmitAsync(0, CancellationToken.None, allowQueue: false, isTrusted: true);

    // A null expectation stands for Environment.ProcessorCount, which is not a compile-time constant.
    [TestCase(null, null, TestName = "Processor count")]
    [TestCase(6, 6, TestName = "Eth module concurrency")]
    [TestCase(0, 1, TestName = "Zero is raised to one")]
    [TestCase(-3, 1, TestName = "Negative is raised to one")]
    public void Permits_follow_eth_module_concurrency(int? ethModuleConcurrentInstances, int? expected)
    {
        using EvmAdmissionGate gate = new(new JsonRpcConfig { EthModuleConcurrentInstances = ethModuleConcurrentInstances });

        Assert.That(gate.Permits, Is.EqualTo(expected ?? Environment.ProcessorCount));
    }

    [TestCase(0, 1, TestName = "No params bytes")]
    [TestCase(BytesPerWeightUnit - 1, 1, TestName = "Just below one unit")]
    [TestCase(BytesPerWeightUnit, 2, TestName = "One unit")]
    [TestCase(BytesPerWeightUnit * 64, MaxWeight, TestName = "Clamped at the heaviest class")]
    public void Weight_grows_with_raw_params_size(int paramsUtf8Length, int expected) =>
        Assert.That(Weigh(paramsUtf8Length), Is.EqualTo(expected));

    [Test]
    public async Task Admits_exactly_the_configured_number_of_permits()
    {
        using EvmAdmissionGate gate = Gate(permits: 2, maxQueueWaitMs: 0);

        using Lease first = await Acquire(gate);
        using Lease second = await Acquire(gate);

        Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
    }

    [Test]
    public async Task In_flight_follows_held_permits()
    {
        using EvmAdmissionGate gate = Gate(permits: 2, maxQueueWaitMs: 0);
        Assert.That(gate.InFlight, Is.Zero);

        Lease first = await Acquire(gate);
        Assert.That(gate.InFlight, Is.EqualTo(1));

        using (await Acquire(gate))
        {
            Assert.That(gate.InFlight, Is.EqualTo(2));
        }

        Assert.That(gate.InFlight, Is.EqualTo(1));
        first.Dispose();
        Assert.That(gate.InFlight, Is.Zero);
    }

    [Test]
    public async Task Releasing_a_permit_admits_the_next_caller()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 0);

        using (Lease held = await Acquire(gate))
        {
            Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
        }

        using Lease afterRelease = await Acquire(gate);
        Assert.Pass();
    }

    [Test]
    public async Task Queued_request_is_admitted_when_a_permit_frees_within_the_budget()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 5_000);
        Lease held = await Acquire(gate);

        ValueTask<Lease> queued = Acquire(gate);
        Assert.That(queued.IsCompleted, Is.False, "the gate is saturated, so this caller must wait");

        held.Dispose();

        using Lease admitted = await queued;
        Assert.Pass();
    }

    [Test]
    public async Task Saturated_gate_sheds_once_the_wait_budget_expires()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 30);
        using Lease held = await Acquire(gate);

        Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
    }

    [TestCase(0, TestName = "Zero budget")]
    [TestCase(-1, TestName = "Negative budget")]
    public async Task A_non_positive_budget_sheds_synchronously(int maxQueueWaitMs)
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs);
        using Lease held = await Acquire(gate);

        Assert.Throws<LimitExceededException>(() => Acquire(gate), "a zero budget must reject on the calling thread");
    }

    [Test]
    public async Task Caller_that_may_not_queue_sheds_immediately_even_with_a_budget()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 60_000);
        using Lease held = await Acquire(gate);

        Assert.That(ShedsOnArrival(gate, allowQueue: false), Is.True);
    }

    /// <summary>A saturated gate must not refuse the trusted caller while it goes on serving anonymous ones.</summary>
    /// <remarks>Shedding authenticated callers reads as a latency favour only while the gate is unsaturated. Saturated,
    /// it meant refusing the consensus client outright while anonymous callers behind it were still admitted after a
    /// wait. They queue at the head instead: no anonymous arrival can overtake one, whatever its weight or arrival time.</remarks>
    [Test]
    public async Task Trusted_caller_is_admitted_before_anonymous_callers_already_waiting()
    {
        using EvmAdmissionGate gate = Gate(permits: 1);
        Lease held = await Acquire(gate);

        ValueTask<Lease> anonymous = Acquire(gate);
        Assert.That(() => gate.Queued, Is.EqualTo(1).After(1000, 10), "precondition: an anonymous caller is queued first");

        ValueTask<Lease> trusted = AcquireTrusted(gate);
        Assert.That(() => gate.Queued, Is.EqualTo(2).After(1000, 10), "the trusted caller queues rather than being shed");

        held.Dispose();

        using Lease admitted = await trusted;
        Assert.That(async () => await anonymous, Throws.InstanceOf<LimitExceededException>(),
            "the one free permit went to the trusted caller, not to the anonymous caller that arrived first");
    }

    /// <summary>A caller that gives up stops waiting, and burns no permit doing so.</summary>
    /// <remarks>Under exactly the overload this gate exists for, clients time out and drop their sockets en masse.
    /// Without the token linked into the wait, each one would still be granted a permit on the next release and execute a
    /// full call for a socket nobody is reading, while a live caller behind it is shed.</remarks>
    [Test]
    public async Task Cancelled_caller_stops_waiting_and_leaves_its_permit_behind()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 60_000);
        Lease held = await Acquire(gate);

        using CancellationTokenSource disconnected = new();
        ValueTask<Lease> queued = Acquire(gate, cancellationToken: disconnected.Token);
        Assert.That(() => gate.Queued, Is.EqualTo(1).After(1000, 10), "precondition: the caller is queued");

        disconnected.Cancel();

        Assert.That(async () => await queued, Throws.InstanceOf<OperationCanceledException>());

        // The abandoned waiter must not have taken the permit with it.
        held.Dispose();
        using Lease next = await Acquire(gate);
        Assert.Pass();
    }

    [Test]
    public void Cancelled_caller_does_not_enter_the_queue()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 60_000);
        using CancellationTokenSource disconnected = new();
        disconnected.Cancel();

        Assert.That(async () => await Acquire(gate, cancellationToken: disconnected.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(gate.Queued, Is.Zero);
    }

    /// <summary>A caller that gives up after the grant but before it resumes must hand the permit straight back.</summary>
    /// <remarks>The only path that both holds a permit and throws: nothing will ever reach the caller to release it, so
    /// a miss here narrows the gate by one for the life of the process. Deterministic because the grant's continuation
    /// is parked on a context the test pumps by hand rather than on the thread pool.</remarks>
    [Test]
    public async Task Caller_that_gives_up_between_the_grant_and_its_resumption_returns_the_permit()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 60_000);
        Lease held = await Acquire(gate);

        using CancellationTokenSource disconnected = new();
        ManualSynchronizationContext resumption = new();
        SynchronizationContext? original = SynchronizationContext.Current;
        Task<Lease> queued;
        try
        {
            SynchronizationContext.SetSynchronizationContext(resumption);
            queued = Acquire(gate, cancellationToken: disconnected.Token).AsTask();
            Assert.That(gate.Queued, Is.EqualTo(1), "precondition: the caller is queued");

            held.Dispose();
            Assert.That(() => resumption.Pending, Is.EqualTo(1).After(1000, 10),
                "precondition: the permit was granted and its resumption is parked, not yet run");

            disconnected.Cancel();
            resumption.RunPending();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        Assert.That(async () => await queued, Throws.InstanceOf<OperationCanceledException>());

        // The caller never got a lease, so if it did not hand the permit back nothing else can.
        using Lease next = await Acquire(gate);
        Assert.Pass();
    }

    /// <summary>Shutdown answers waiters at once instead of waiting out the budget for each of them.</summary>
    [Test]
    public async Task Disposing_the_gate_answers_everyone_waiting_as_shed()
    {
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 60_000);
        using Lease held = await Acquire(gate);

        ValueTask<Lease> queued = Acquire(gate);
        Assert.That(() => gate.Queued, Is.EqualTo(1).After(1000, 10));

        gate.Dispose();

        // Bounded rather than awaited outright: without the drain this sits out the whole 60 s budget, and a regression
        // should fail here rather than hang the suite.
        Task<Lease> queuedTask = queued.AsTask();
        Assert.That(await Task.WhenAny(queuedTask, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(queuedTask),
            "a closing gate must answer its waiters rather than let them wait out the budget");
        Assert.That(async () => await queuedTask, Throws.InstanceOf<LimitExceededException>(),
            "a shed answer maps to 'Too many requests', not to an internal error");
        Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>(), "a closed gate admits nobody");
    }

    [Test]
    public async Task Waiters_of_equal_weight_are_admitted_in_arrival_order([Values] bool frozenClock)
    {
        // Weight decides the ordering only between cost classes; within one class the deadline reduces to arrival time,
        // so equal-weight callers keep strict FIFO. The frozen-clock case needs the sequence tiebreaker: with every
        // deadline identical, PriorityQueue - not a stable heap - has no order of its own to fall back on.
        TestClock? clock = frozenClock ? new TestClock() : null;
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000, clock);
        Lease held = await Acquire(gate);

        const int waiterCount = 8;
        ValueTask<Lease>[] waiters = new ValueTask<Lease>[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            waiters[i] = Acquire(gate);
            Assert.That(waiters[i].IsCompleted, Is.False);
        }

        // Admission resumes asynchronously by design (see Waiter), so order is observed rather than polled.
        List<int> admissionOrder = [];
        Task[] observers = new Task[waiterCount];
        for (int i = 0; i < waiterCount; i++)
        {
            int index = i;
            ValueTask<Lease> waiter = waiters[i];
            observers[i] = Task.Run(async () =>
            {
                using Lease lease = await waiter;
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
        // The reason for ordering by cost at all: a small eth_call must not sit behind large simulations.
        TestClock clock = new();
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000, clock);
        Lease held = await Acquire(gate);

        ValueTask<Lease> heavy = Acquire(gate, MaxWeight);
        ValueTask<Lease> light = Acquire(gate);

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
        // Unaged cost ordering lets every later light caller overtake forever, so a heavy request would shed at its
        // budget for as long as light load continued; anchoring the deadline to arrival bounds the overtaking window.
        const int budgetMs = 30_000;
        TestClock clock = new();
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: budgetMs, clock);
        Lease held = await Acquire(gate);

        ValueTask<Lease> heavy = Acquire(gate, MaxWeight);

        // Past the heavy caller's slack window, so any later light caller now has the later deadline.
        clock.Advance(Quantum(budgetMs) * MaxWeight);
        ValueTask<Lease> lateLight = Acquire(gate);

        held.Dispose();

        using (await heavy)
        {
            Assert.That(lateLight.IsCompleted, Is.False, "the aged-in heavy caller must go first");
        }

        (await lateLight).Dispose();
    }

    [Test]
    public async Task The_heaviest_class_can_be_overtaken_for_just_under_half_its_budget()
    {
        // Pins the constant the ordering was tuned to: with QuantumsPerBudget = 2 * MaxWeight a light arrival inside
        // (MaxWeight - 1) quanta still overtakes, one at MaxWeight quanta no longer does - the same aging point the
        // bucketed predecessor used, so the measured median-latency behaviour carries over.
        const int budgetMs = 30_000;
        TestClock clock = new();
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: budgetMs, clock);
        Lease held = await Acquire(gate);

        ValueTask<Lease> heavy = Acquire(gate, MaxWeight);
        clock.Advance(Quantum(budgetMs) * (MaxWeight - 1) - 1);
        ValueTask<Lease> insideWindow = Acquire(gate);

        held.Dispose();

        using (await insideWindow)
        {
            Assert.That(heavy.IsCompleted, Is.False, "an arrival inside the slack window overtakes");
        }

        (await heavy).Dispose();
    }

    [Test]
    public async Task A_heavy_waiter_is_served_after_the_light_callers_that_legitimately_overtook_it()
    {
        const int budgetMs = 30_000;
        TestClock clock = new();
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: budgetMs, clock);
        Lease held = await Acquire(gate);

        ValueTask<Lease> heavy = Acquire(gate, MaxWeight);
        Assert.That(heavy.IsCompleted, Is.False);

        // Inside the heavy caller's slack window, so these are entitled to overtake it.
        const int overtakerCount = 3;
        ValueTask<Lease>[] overtakers = new ValueTask<Lease>[overtakerCount];
        for (int i = 0; i < overtakerCount; i++)
        {
            overtakers[i] = Acquire(gate);
        }

        // Past the window: these must not overtake, however many of them arrive.
        clock.Advance(Quantum(budgetMs) * MaxWeight);
        ValueTask<Lease>[] lateArrivals = new ValueTask<Lease>[overtakerCount];
        for (int i = 0; i < overtakerCount; i++)
        {
            lateArrivals[i] = Acquire(gate);
        }

        held.Dispose();

        for (int i = 0; i < overtakerCount; i++)
        {
            (await overtakers[i]).Dispose();
        }

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
        using EvmAdmissionGate gate = Gate(permits: 1, maxQueueWaitMs: 30_000);
        Lease held = await Acquire(gate);

        ValueTask<Lease>[] queued = new ValueTask<Lease>[MaxQueueDepthPerPermit];
        for (int i = 0; i < queued.Length; i++)
        {
            queued[i] = Acquire(gate);
            Assert.That(queued[i].IsCompleted, Is.False);
        }

        // Asserted on the synchronous throw, not by awaiting: a caller that queues instead raises the same exception once
        // the budget expires, so awaiting cannot tell "refused on arrival" from "waited 30 s".
        Assert.That(ShedsOnArrival(gate), Is.True, "over the cap the gate must refuse on arrival");
        Assert.That(gate.Queued, Is.EqualTo(queued.Length), "the shed caller must not have been queued");

        // Drained rather than abandoned: an un-awaited waiter faults a whole budget later.
        held.Dispose();
        for (int i = 0; i < queued.Length; i++)
        {
            (await queued[i]).Dispose();
        }
    }

    /// <summary>Whether the gate refuses without queueing: <see cref="EvmAdmissionGate.AdmitAsync"/> throws at the call
    /// itself rather than from the returned task after the wait budget.</summary>
    private static bool ShedsOnArrival(EvmAdmissionGate gate, bool allowQueue = true)
    {
        ValueTask<Lease> pending;
        try
        {
            pending = gate.AdmitAsync(0, CancellationToken.None, allowQueue);
        }
        catch (LimitExceededException)
        {
            return true;
        }

        // "Did not throw" alone would also cover a free permit, which would mean the caller never tested the saturated
        // gate it meant to.
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
        // Exercises the paths that maintain the live count concurrently - enqueue under the lock, expiry from the timer
        // thread, release from the holder - and checks the count settles and the gate is still usable.
        using EvmAdmissionGate gate = Gate(permits: 2, maxQueueWaitMs: 40);
        Lease first = await Acquire(gate);
        Lease second = await Acquire(gate);

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
                        (await Acquire(gate)).Dispose();
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
        // asynchronously, so a worker can finish while the settling thread is still a statement short.
        Assert.That(() => gate.Queued, Is.Zero.After(2000, 50),
            "a lost decrement never recovers, so the depth cap would eventually refuse every arrival");

        using Lease afterwards = await Acquire(gate);
        Assert.That(gate.InFlight, Is.EqualTo(1));
    }

    [Test]
    public async Task Releasing_a_lease_twice_throws_rather_than_inflating_the_permit_count()
    {
        using EvmAdmissionGate gate = Gate(permits: 1);
        Lease lease = await Acquire(gate);
        lease.Dispose();

        Assert.That(lease.Dispose, Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Disposing_the_default_lease_is_a_no_op() =>
        Assert.That(() => default(Lease).Dispose(), Throws.Nothing);

    [TestCase(0, TestName = "Zero instances")]
    [TestCase(-5, TestName = "Negative instances")]
    public async Task Non_positive_instance_count_still_admits_one_caller(int configured)
    {
        using EvmAdmissionGate gate = Gate(permits: configured, maxQueueWaitMs: 0);

        using Lease only = await Acquire(gate);
        Assert.That(async () => await Acquire(gate), Throws.InstanceOf<LimitExceededException>());
    }
}
