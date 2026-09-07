// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Threading;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;
using static Nethermind.JsonRpc.EvmAdmissionGate;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class EvmAdmissionGateTests
{
    private const int EvmPermits = 2;
    private const int MaxQueueWaitMs = 5_000;
    private const int QueueLimit = 3;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(MaxQueueWaitMs);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    [ThreadStatic]
    private static bool _releasingPermit;

    private EvmAdmissionGate _gate = null!;
    private ManualTimeProvider _timeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new ManualTimeProvider();
        _gate = CreateGate(new JsonRpcConfig { EvmExecutionConcurrency = EvmPermits, EthModuleConcurrentInstances = EvmPermits, EvmExecutionMaxQueueWaitMs = MaxQueueWaitMs });
    }

    [TearDown]
    public void TearDown() => _gate.Dispose();

    [TestCase("eth_call", true)]
    [TestCase("eth_estimateGas", true)]
    [TestCase("eth_createAccessList", true)]
    [TestCase("eth_simulateV1", true)]
    [TestCase("eth_fillTransaction", true)]
    [TestCase("debug_simulateV1", true)]
    [TestCase("eth_blockNumber", false)]
    [TestCase("eth_getBalance", false)]
    [TestCase("eth_getLogs", false)]
    [TestCase("eth_sendRawTransaction", false)]
    [TestCase("eth_getProof", false)]
    [TestCase("debug_traceCall", false)]
    [TestCase("debug_traceTransaction", false)]
    [TestCase("trace_call", false)]
    [TestCase("trace_replayBlockTransactions", false)]
    [TestCase("proof_call", false)]
    [TestCase("debug_getRawBlock", false)]
    [TestCase("engine_newPayloadV4", false)]
    [TestCase("net_version", false)]
    public void Only_methods_flagged_as_evm_execution_are_gated(string methodName, bool gated) =>
        Assert.That(ModuleProvider.Resolve(methodName)!.IsEvmExecution, Is.EqualTo(gated));

    // A null expectation stands for Environment.ProcessorCount, which is not a compile-time constant.
    [TestCase(null, null, null, TestName = "Processor count")]
    [TestCase(null, 6, 6, TestName = "Falls back to EthModuleConcurrentInstances")]
    [TestCase(4, 6, 4, TestName = "Explicit value wins")]
    [TestCase(0, 6, 1, TestName = "Zero is raised to one")]
    [TestCase(-3, 6, 1, TestName = "Negative is raised to one")]
    [TestCase(32, 6, 6, TestName = "Lowered to the env-pool cap")]
    [TestCase(6, 4, 4, TestName = "Lowered to a smaller env-pool cap")]
    public void Permits_follow_the_config_chain(int? evmExecutionConcurrency, int? ethModuleConcurrentInstances, int? expected)
    {
        EvmAdmissionGate gate = CreateGate(new JsonRpcConfig { EvmExecutionConcurrency = evmExecutionConcurrency, EthModuleConcurrentInstances = ethModuleConcurrentInstances });

        Assert.That(gate.Permits, Is.EqualTo(expected ?? Environment.ProcessorCount));
    }

    [TestCase(null, 500, TestName = "Defaults to 500 ms")]
    [TestCase(-1, 0, TestName = "Negative disables queueing")]
    public async Task Wait_budget_follows_the_config(int? maxQueueWaitMs, int expectedBudgetMs)
    {
        JsonRpcConfig config = new() { EvmExecutionConcurrency = 1, EthModuleConcurrentInstances = 1 };
        if (maxQueueWaitMs is int configured)
        {
            config.EvmExecutionMaxQueueWaitMs = configured;
        }
        EvmAdmissionGate gate = CreateGate(config);
        using Lease held = await Admit(gate);

        if (expectedBudgetMs == 0)
        {
            Assert.Throws<LimitExceededException>(() => Admit(gate), "a zero budget must reject synchronously");
            return;
        }

        Task<Lease> waiting = Admit(gate).AsTask();
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(expectedBudgetMs - 1));
        Assert.That(waiting.IsCompleted, Is.False, "the waiter must survive until the budget");
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(1));
        Assert.ThrowsAsync<LimitExceededException>(() => waiting);
    }

    [TestCase(0, 1, TestName = "No params bytes")]
    [TestCase(BytesPerWeightUnit - 1, 1, TestName = "Just below one unit")]
    [TestCase(BytesPerWeightUnit, 2, TestName = "One unit")]
    [TestCase(4 * BytesPerWeightUnit + 17, 5, TestName = "Partial units round down")]
    [TestCase(7 * BytesPerWeightUnit, 8, TestName = "Upper clamp reached exactly")]
    [TestCase(int.MaxValue, 8, TestName = "Upper clamp")]
    public void Weight_grows_with_raw_params_size(int paramsUtf8Length, int expectedWeight) =>
        Assert.That(Weigh(paramsUtf8Length), Is.EqualTo(expectedWeight));

    [Test]
    public async Task Permits_are_respected_and_released_on_dispose()
    {
        Lease[] held = new Lease[EvmPermits];
        for (int i = 0; i < EvmPermits; i++)
        {
            ValueTask<Lease> admission = Admit();
            Assert.That(admission.IsCompletedSuccessfully, Is.True, $"permit {i} should be granted synchronously");
            held[i] = admission.Result;
        }

        Task<Lease> waiting = Admit().AsTask();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.IsCompleted, Is.False, "one over the permit count must wait");
            Assert.That(_gate.InFlight, Is.EqualTo(EvmPermits));
            Assert.That(_gate.Queued, Is.EqualTo(1));
        }

        held[0].Dispose();
        using Lease admitted = await waiting.WaitAsync(WaitBudget);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.InFlight, Is.EqualTo(EvmPermits));
            Assert.That(_gate.Queued, Is.EqualTo(0));
        }

        held[1].Dispose();
    }

    [Test]
    [NonParallelizable]
    public async Task Cancelled_waiter_is_skipped_at_the_next_grant_and_never_takes_a_permit()
    {
        long cancellationsBefore = Metrics.RpcAdmissionCancellations;
        EvmAdmissionGate gate = CreateGate(SinglePermit());
        using CancellationTokenSource cancellation = new();
        Lease held = await Admit(gate);

        Task<Lease> waiting = Admit(gate, cancellationToken: cancellation.Token).AsTask();
        cancellation.Cancel();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.IsCompleted, Is.False, "cancellation is observed lazily, at the next grant or sweep");
            Assert.That(gate.Queued, Is.EqualTo(1));
        }

        held.Dispose();

        Assert.CatchAsync<OperationCanceledException>(() => waiting.WaitAsync(WaitBudget));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.Queued, Is.EqualTo(0));
            Assert.That(gate.InFlight, Is.EqualTo(0), "no live waiter remained, so the permit must have been returned");
            Assert.That(Metrics.RpcAdmissionCancellations, Is.EqualTo(cancellationsBefore + 1));
        }

        ValueTask<Lease> fresh = Admit(gate);
        Assert.That(fresh.IsCompletedSuccessfully, Is.True, "the cancelled waiter must not have taken the freed permit");
        fresh.Result.Dispose();

        // A sweep racing the grant must find nothing left to settle.
        _timeProvider.AdvanceAndFireTimer(Budget);
        Assert.That(gate.Queued, Is.EqualTo(0));
    }

    [TestCase(MinWeight, TestName = "Live waiter in the same bucket")]
    [TestCase(MaxWeight, TestName = "Live waiter in a heavier bucket")]
    public async Task Grant_skips_a_cancelled_head_and_passes_the_permit_to_the_next_live_waiter(int liveWeight)
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit());
        using CancellationTokenSource cancellation = new();
        Lease held = await Admit(gate);
        Task<Lease> cancelled = Admit(gate, cancellationToken: cancellation.Token).AsTask();
        Task<Lease> live = Admit(gate, liveWeight).AsTask();
        cancellation.Cancel();

        held.Dispose();

        using Lease granted = await live.WaitAsync(WaitBudget);
        Assert.CatchAsync<OperationCanceledException>(() => cancelled.WaitAsync(WaitBudget));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.Queued, Is.EqualTo(0));
            Assert.That(gate.InFlight, Is.EqualTo(1), "the permit passed straight on; returning it as well would leave the live lease uncounted");
        }
    }

    [TestCase(false, TestName = "Before its deadline")]
    [TestCase(true, TestName = "At its deadline: a cancellation, not a rejection")]
    [NonParallelizable]
    public async Task Cancelled_waiter_is_settled_by_the_sweep(bool atDeadline)
    {
        long rejectionsBefore = Metrics.RpcAdmissionWaitTimeoutRejections;
        long cancellationsBefore = Metrics.RpcAdmissionCancellations;
        EvmAdmissionGate gate = CreateGate(SinglePermit());
        using CancellationTokenSource cancellation = new();
        using Lease held = await Admit(gate);
        Task<Lease> waiting = Admit(gate, cancellationToken: cancellation.Token).AsTask();
        // Behind the cancelled head in the same bucket, with half its budget left when the head is popped.
        _timeProvider.Advance(Budget / 2);
        Task<Lease> live = Admit(gate).AsTask();

        cancellation.Cancel();
        _timeProvider.AdvanceAndFireTimer(atDeadline ? Budget / 2 : TimeSpan.Zero);

        Assert.CatchAsync<OperationCanceledException>(() => waiting.WaitAsync(WaitBudget));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(live.IsCompleted, Is.False, "the live waiter behind the cancelled head keeps waiting");
            Assert.That(gate.Queued, Is.EqualTo(1));
            Assert.That(gate.InFlight, Is.EqualTo(1));
            Assert.That(Metrics.RpcAdmissionWaitTimeoutRejections, Is.EqualTo(rejectionsBefore));
            Assert.That(Metrics.RpcAdmissionCancellations, Is.EqualTo(cancellationsBefore + 1));
        }
    }

    [TestCase(0, TestName = "Zero budget: shed on the calling thread, nothing queued")]
    [TestCase(100, TestName = "Positive budget: shed by the wait timeout")]
    public async Task Rejects_when_permits_never_free(int maxQueueWaitMs)
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit(maxQueueWaitMs));
        long rejectionsBefore = maxQueueWaitMs == 0 ? Metrics.RpcAdmissionQueueFullRejections : Metrics.RpcAdmissionWaitTimeoutRejections;
        Lease held = await Admit(gate);

        if (maxQueueWaitMs == 0)
        {
            Assert.Throws<LimitExceededException>(() => Admit(gate), "a zero budget must reject synchronously, without a waiter");
        }
        else
        {
            Task<Lease> waiting = Admit(gate).AsTask();
            Assert.That(waiting.IsCompleted, Is.False);
            _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(maxQueueWaitMs));
            Assert.ThrowsAsync<LimitExceededException>(() => waiting);
        }

        long rejectionsAfter = maxQueueWaitMs == 0 ? Metrics.RpcAdmissionQueueFullRejections : Metrics.RpcAdmissionWaitTimeoutRejections;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejectionsAfter, Is.GreaterThan(rejectionsBefore));
            Assert.That(gate.Queued, Is.EqualTo(0));
            Assert.That(gate.InFlight, Is.EqualTo(1));
        }

        held.Dispose();
        ValueTask<Lease> fresh = Admit(gate);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fresh.IsCompletedSuccessfully, Is.True, "the freed permit must not have gone to the timed-out waiter");
            Assert.That(gate.InFlight, Is.EqualTo(1));
        }
        fresh.Result.Dispose();
    }

    [TestCase(QueueLimit, TestName = "EvmExecutionQueueLimit caps the waiters")]
    [TestCase(0, TestName = "EvmExecutionQueueLimit zero lifts the cap")]
    public async Task Queued_waiters_are_capped_by_the_queue_limit(int queueLimit)
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit(queueLimit: queueLimit));
        long rejectionsBefore = Metrics.RpcAdmissionQueueFullRejections;
        Lease held = await Admit(gate);
        List<Task<Lease>> queued = [];
        for (int i = 0; i < QueueLimit; i++)
        {
            queued.Add(Admit(gate).AsTask());
        }
        Assert.That(gate.Queued, Is.EqualTo(QueueLimit), "waiters up to the limit must be queued");

        if (queueLimit == 0)
        {
            queued.Add(Admit(gate).AsTask());
            Assert.That(gate.Queued, Is.EqualTo(QueueLimit + 1));
        }
        else
        {
            Assert.Throws<LimitExceededException>(() => Admit(gate), "the waiter over the limit must be shed synchronously");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Metrics.RpcAdmissionQueueFullRejections, Is.GreaterThan(rejectionsBefore));
                Assert.That(gate.Queued, Is.EqualTo(QueueLimit));
            }
        }

        held.Dispose();
        foreach (Task<Lease> waiter in queued)
        {
            (await waiter.WaitAsync(WaitBudget)).Dispose();
        }
        Assert.That(gate.InFlight, Is.EqualTo(0));
    }

    [TestCase(0, TestName = "Uncapped queue")]
    [TestCase(QueueLimit, TestName = "Capped queue")]
    public async Task Every_waiter_expires_at_the_budget(int queueLimit)
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit(queueLimit: queueLimit));
        using Lease held = await Admit(gate);
        List<Task<Lease>> queued = [];
        for (int i = 0; i < QueueLimit; i++)
        {
            queued.Add(Admit(gate).AsTask());
        }

        if (queueLimit == 0)
        {
            queued.Add(Admit(gate).AsTask());
        }
        else
        {
            Assert.Throws<LimitExceededException>(() => Admit(gate), "the waiter over the cap must be shed synchronously");
        }
        Assert.That(gate.Queued, Is.EqualTo(queued.Count));

        _timeProvider.AdvanceAndFireTimer(Budget);

        foreach (Task<Lease> waiter in queued)
        {
            Assert.ThrowsAsync<LimitExceededException>(() => waiter.WaitAsync(WaitBudget));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.Queued, Is.EqualTo(0));
            Assert.That(gate.InFlight, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Expired_waiters_are_swept_per_bucket()
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit());
        using Lease held = await Admit(gate);
        Task<Lease> heavy = Admit(gate, MaxWeight).AsTask();
        _timeProvider.Advance(Budget / 2);
        Task<Lease> light = Admit(gate, MinWeight).AsTask();

        _timeProvider.AdvanceAndFireTimer(Budget / 2);
        Assert.ThrowsAsync<LimitExceededException>(() => heavy);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(light.IsCompleted, Is.False, "the light waiter has half its budget left");
            Assert.That(gate.Queued, Is.EqualTo(1));
        }

        _timeProvider.AdvanceAndFireTimer(Budget / 2);
        Assert.ThrowsAsync<LimitExceededException>(() => light);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.Queued, Is.EqualTo(0));
            Assert.That(gate.InFlight, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Sweep_rearms_for_the_remaining_deadline_and_never_disarms()
    {
        RecordingTimeProvider timeProvider = new();
        EvmAdmissionGate gate = CreateGate(SinglePermit(), timeProvider);
        Lease held = await Admit(gate);

        Task<Lease> first = Admit(gate).AsTask();
        Assert.That(timeProvider.DueTimes, Is.EqualTo(new[] { Budget }), "enqueueing into an empty queue arms the sweep for one budget");

        timeProvider.Advance(Budget / 2);
        Task<Lease> second = Admit(gate).AsTask();
        Assert.That(timeProvider.DueTimes, Has.Count.EqualTo(1), "enqueueing behind a waiter leaves the timer alone");

        held.Dispose();
        held = await first.WaitAsync(WaitBudget);
        Assert.That(timeProvider.DueTimes, Has.Count.EqualTo(1), "a grant leaves the timer alone");

        // A stale fire for the granted waiter: nothing expires, yet the sweep re-arms for the remaining one.
        timeProvider.Advance(Budget / 2);
        timeProvider.FireTimer();
        Assert.That(timeProvider.DueTimes, Is.EqualTo(new[] { Budget, Budget / 2 }));

        Task<Lease> third = Admit(gate).AsTask();
        timeProvider.Advance(Budget / 2);
        timeProvider.FireTimer();
        Assert.ThrowsAsync<LimitExceededException>(() => second);
        Assert.That(timeProvider.DueTimes, Is.EqualTo(new[] { Budget, Budget / 2, Budget / 2 }), "popping one of two re-arms for the remaining deadline");

        timeProvider.Advance(Budget / 2);
        timeProvider.FireTimer();
        Assert.ThrowsAsync<LimitExceededException>(() => third);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(timeProvider.DueTimes, Has.Count.EqualTo(3), "a sweep that leaves nothing queued does not touch the timer");
            Assert.That(timeProvider.DueTimes, Has.None.EqualTo(Timeout.InfiniteTimeSpan));
            Assert.That(gate.Queued, Is.EqualTo(0));
        }

        held.Dispose();
    }

    // System.Threading.Timer truncates a due time to whole milliseconds; a fractional remainder would fire early and re-fire
    // at zero until the clock passes the deadline.
    [Test]
    public async Task Sweep_rearms_for_a_whole_number_of_milliseconds()
    {
        TimeSpan fraction = TimeSpan.FromMicroseconds(300);
        RecordingTimeProvider timeProvider = new();
        EvmAdmissionGate gate = CreateGate(SinglePermit(), timeProvider);
        using Lease held = await Admit(gate);
        Task<Lease> first = Admit(gate).AsTask();
        timeProvider.Advance(Budget / 2 + fraction);
        Task<Lease> second = Admit(gate).AsTask();

        timeProvider.Advance(Budget / 2 - fraction);
        timeProvider.FireTimer();

        Assert.ThrowsAsync<LimitExceededException>(() => first);
        Assert.That(timeProvider.DueTimes[^1], Is.EqualTo(Budget / 2 + TimeSpan.FromMilliseconds(1)), "the second waiter's remaining budget must be rounded up to the timer's resolution, not truncated by it");

        timeProvider.Advance(timeProvider.DueTimes[^1]);
        timeProvider.FireTimer();
        Assert.ThrowsAsync<LimitExceededException>(() => second);
    }

    [Test]
    public void Disposing_the_gate_disposes_its_timer()
    {
        RecordingTimeProvider timeProvider = new();
        EvmAdmissionGate gate = CreateGate(SinglePermit(), timeProvider);

        gate.Dispose();

        Assert.That(timeProvider.TimerDisposed, Is.True);
    }

    [Test]
    public async Task Grant_reaching_an_expired_waiter_serves_it()
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit());
        Lease held = await Admit(gate);
        Task<Lease> waiting = Admit(gate).AsTask();

        _timeProvider.Advance(Budget);
        held.Dispose();

        using Lease granted = await waiting.WaitAsync(WaitBudget);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.Queued, Is.EqualTo(0));
            Assert.That(gate.InFlight, Is.EqualTo(1));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Queued_and_in_flight_gauges_follow_the_gate()
    {
        Lease[] held = [await Admit(), await Admit()];
        Task<Lease> waiting = Admit().AsTask();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.RpcAdmissionInFlight, Is.EqualTo(EvmPermits));
            Assert.That(Metrics.RpcAdmissionQueued, Is.EqualTo(1));
        }

        held[0].Dispose();
        held[1].Dispose();
        (await waiting.WaitAsync(WaitBudget)).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.RpcAdmissionInFlight, Is.EqualTo(0));
            Assert.That(Metrics.RpcAdmissionQueued, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Lighter_waiters_are_served_first_and_fifo_within_a_weight()
    {
        Lease[] held = [await Admit(MaxWeight), await Admit(MaxWeight)];
        Task<Lease> heavyFirst = Admit(MaxWeight).AsTask();
        Task<Lease> heavySecond = Admit(MaxWeight).AsTask();
        Task<Lease> lightFirst = Admit(MinWeight).AsTask();
        Task<Lease> lightSecond = Admit(MinWeight).AsTask();
        Task<Lease>[] expectedOrder = [lightFirst, lightSecond, heavyFirst, heavySecond];
        Assert.That(_gate.Queued, Is.EqualTo(expectedOrder.Length));

        Lease releasing = held[0];
        for (int i = 0; i < expectedOrder.Length; i++)
        {
            releasing.Dispose();
            releasing = await expectedOrder[i].WaitAsync(WaitBudget);
            for (int later = i + 1; later < expectedOrder.Length; later++)
            {
                Assert.That(expectedOrder[later].IsCompleted, Is.False, $"waiter {later} must not be admitted before waiter {i}");
            }
        }

        releasing.Dispose();
        held[1].Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.Queued, Is.EqualTo(0));
            Assert.That(_gate.InFlight, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Overtaken_heavy_waiter_is_shed_at_its_wait_budget_while_light_traffic_keeps_flowing()
    {
        const int budgetMs = 200;
        EvmAdmissionGate gate = CreateGate(SinglePermit(budgetMs));
        Lease holder = await Admit(gate);
        Task<Lease> heavy = Admit(gate, MaxWeight).AsTask();

        try
        {
            Assert.That(heavy.IsCompleted, Is.False);
            int lightServed = 0;
            while (lightServed < 2)
            {
                Task<Lease> light = Admit(gate).AsTask();
                Assert.That(light.IsCompleted, Is.False);
                holder.Dispose();
                holder = await light.WaitAsync(WaitBudget);
                lightServed++;
            }

            Assert.That(heavy.IsCompleted, Is.False);
            _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(budgetMs));

            Assert.ThrowsAsync<LimitExceededException>(() => heavy);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lightServed, Is.EqualTo(2));
                Assert.That(gate.Queued, Is.EqualTo(0));
                Assert.That(gate.InFlight, Is.EqualTo(1));
            }
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Test]
    public async Task Timeouts_racing_grants_neither_leak_nor_double_release_permits()
    {
        const int requests = 2_000;
        EvmAdmissionGate gate = CreateGate(new JsonRpcConfig { EvmExecutionConcurrency = EvmPermits, EthModuleConcurrentInstances = EvmPermits, EvmExecutionMaxQueueWaitMs = 1 }, TimeProvider.System);
        int admitted = 0;
        int shed = 0;
        Task[] callers = new Task[requests];
        for (int i = 0; i < requests; i++)
        {
            int weight = i % MaxWeight + 1;
            callers[i] = Task.Run(async () =>
            {
                try
                {
                    using (await gate.AdmitAsync(ParamsBytes(weight), CancellationToken.None))
                    {
                        Interlocked.Increment(ref admitted);
                        await Task.Yield();
                    }
                }
                catch (LimitExceededException)
                {
                    Interlocked.Increment(ref shed);
                }
            });
        }

        await Task.WhenAll(callers).WaitAsync(WaitBudget);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(admitted + shed, Is.EqualTo(requests));
            Assert.That(admitted, Is.GreaterThan(0));
            Assert.That(shed, Is.GreaterThan(0));
            // Deliberately not asserting which shed path ran: the default queue limit can reject up front before any
            // 1 ms budget expires.
            Assert.That(gate.InFlight, Is.EqualTo(0));
            Assert.That(gate.Queued, Is.EqualTo(0));
        }

        for (int i = 0; i < EvmPermits; i++)
        {
            ValueTask<Lease> fresh = Admit(gate);
            Assert.That(fresh.IsCompletedSuccessfully, Is.True, $"permit {i} must be available again");
            fresh.Result.Dispose();
        }
    }

    [Test]
    public async Task Releasing_a_permit_never_runs_the_next_waiters_continuation_inline()
    {
        Lease[] held = [await Admit(), await Admit()];
        bool? continuationRanOnReleaser = null;
        Task<Lease> probe = Admit().AsTask().ContinueWith(admission =>
        {
            continuationRanOnReleaser = _releasingPermit;
            return admission.Result;
        }, TaskContinuationOptions.ExecuteSynchronously);

        _releasingPermit = true;
        held[0].Dispose();
        _releasingPermit = false;

        using (await probe.WaitAsync(WaitBudget))
        {
            Assert.That(continuationRanOnReleaser, Is.False);
        }
        held[1].Dispose();
    }

    private EvmAdmissionGate CreateGate(JsonRpcConfig config, TimeProvider? timeProvider = null) =>
        new(config, LimboLogs.Instance, timeProvider ?? _timeProvider);

    private static JsonRpcConfig SinglePermit(int maxQueueWaitMs = MaxQueueWaitMs, int queueLimit = 500) => new()
    {
        EvmExecutionConcurrency = 1,
        EthModuleConcurrentInstances = 1,
        EvmExecutionMaxQueueWaitMs = maxQueueWaitMs,
        EvmExecutionQueueLimit = queueLimit,
    };

    private ValueTask<Lease> Admit(int weight = MinWeight) => Admit(_gate, weight);

    private static ValueTask<Lease> Admit(EvmAdmissionGate gate, int weight = MinWeight, CancellationToken cancellationToken = default) =>
        gate.AdmitAsync(ParamsBytes(weight), cancellationToken);

    // The smallest params size that weighs the given amount.
    private static int ParamsBytes(int weight) => (weight - MinWeight) * BytesPerWeightUnit;

    private static readonly RpcModuleProvider ModuleProvider = CreateModuleProvider();

    private static RpcModuleProvider CreateModuleProvider()
    {
        RpcModuleProvider provider = new(new RealFileSystem(), new JsonRpcConfig(), new EthereumJsonSerializer(), LimboLogs.Instance);
        provider.Register(new SingletonModulePool<IEthRpcModule>(Substitute.For<IEthRpcModule>()));
        provider.Register(new SingletonModulePool<IDebugRpcModule>(Substitute.For<IDebugRpcModule>()));
        provider.Register(new SingletonModulePool<ITraceRpcModule>(Substitute.For<ITraceRpcModule>()));
        provider.Register(new SingletonModulePool<IProofRpcModule>(Substitute.For<IProofRpcModule>()));
        provider.Register(new SingletonModulePool<IEngineRpcModule>(Substitute.For<IEngineRpcModule>()));
        provider.Register(new SingletonModulePool<INetRpcModule>(Substitute.For<INetRpcModule>()));
        return provider;
    }

    // Records every re-arm so the arming rules the sweep relies on can be asserted; ManualTimeProvider ignores Change.
    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _dueTimes = [];
        private long _ticks;
        private TimerCallback? _callback;
        private object? _state;

        public IReadOnlyList<TimeSpan> DueTimes => _dueTimes;
        public bool TimerDisposed { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;

        public void FireTimer() => _callback!(_state);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new RecordingTimer(this);
        }

        private sealed class RecordingTimer(RecordingTimeProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner._dueTimes.Add(dueTime);
                return true;
            }

            public void Dispose() => owner.TimerDisposed = true;

            public ValueTask DisposeAsync() => default;
        }
    }
}
