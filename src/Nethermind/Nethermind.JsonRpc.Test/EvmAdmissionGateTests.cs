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
using NUnit.Framework;
using NUnit.Framework.Constraints;
using static Nethermind.JsonRpc.EvmAdmissionGate;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

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
        _gate = CreateGate(new JsonRpcConfig { EvmExecutionConcurrency = EvmPermits, EthModuleConcurrentInstances = EvmPermits, MaxQueueWaitMs = MaxQueueWaitMs });
    }

    [TestCase(typeof(IEthRpcModule), "eth_call", true)]
    [TestCase(typeof(IEthRpcModule), "eth_estimateGas", true)]
    [TestCase(typeof(IEthRpcModule), "eth_createAccessList", true)]
    [TestCase(typeof(IEthRpcModule), "eth_simulateV1", true)]
    [TestCase(typeof(IEthRpcModule), "eth_fillTransaction", true)]
    [TestCase(typeof(IDebugRpcModule), "debug_simulateV1", true)]
    [TestCase(typeof(IEthRpcModule), "eth_blockNumber", false)]
    [TestCase(typeof(IEthRpcModule), "eth_getBalance", false)]
    [TestCase(typeof(IEthRpcModule), "eth_getLogs", false)]
    [TestCase(typeof(IEthRpcModule), "eth_sendRawTransaction", false)]
    [TestCase(typeof(IEthRpcModule), "eth_getProof", false)]
    [TestCase(typeof(IDebugRpcModule), "debug_traceCall", false)]
    [TestCase(typeof(IDebugRpcModule), "debug_traceTransaction", false)]
    [TestCase(typeof(ITraceRpcModule), "trace_call", false)]
    [TestCase(typeof(ITraceRpcModule), "trace_replayBlockTransactions", false)]
    [TestCase(typeof(IProofRpcModule), "proof_call", false)]
    [TestCase(typeof(IDebugRpcModule), "debug_getRawBlock", false)]
    [TestCase(typeof(IEngineRpcModule), "engine_newPayloadV4", false)]
    [TestCase(typeof(INetRpcModule), "net_version", false)]
    public void Only_the_six_evm_execution_methods_are_gated(Type moduleType, string methodName, bool gated) =>
        Assert.That(Resolve(moduleType, methodName).IsEvmExecution, Is.EqualTo(gated));

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
            config.MaxQueueWaitMs = configured;
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
    public async Task Cancelled_waiter_is_skipped_at_the_next_grant_and_never_takes_a_permit()
    {
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
        }
    }

    [Test]
    public async Task Rejects_immediately_when_predicted_wait_exceeds_budget()
    {
        long rejectionsBefore = Metrics.RpcAdmissionPredictedWaitRejections;
        // Two permits: the first waiter has nothing queued ahead and always queues, however slow the gate; the second
        // one predicts a wait of EWMA / 2, so this makes it 2x the budget.
        _gate.SetServiceTimeMs(4.0 * MaxQueueWaitMs);
        Lease[] held = [await Admit(), await Admit()];
        Task<Lease> queued = Admit().AsTask();

        Assert.Throws<LimitExceededException>(() => Admit());
        Assert.That(Metrics.RpcAdmissionPredictedWaitRejections, Is.GreaterThan(rejectionsBefore));

        held[0].Dispose();
        held[1].Dispose();
        (await queued.WaitAsync(WaitBudget)).Dispose();
    }

    [TestCase(0, TestName = "Zero budget: shed on the calling thread, nothing queued")]
    [TestCase(100, TestName = "Positive budget: shed by the wait timeout")]
    public async Task Rejects_when_permits_never_free(int maxQueueWaitMs)
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit(maxQueueWaitMs));
        long rejectionsBefore = maxQueueWaitMs == 0 ? Metrics.RpcAdmissionPredictedWaitRejections : Metrics.RpcAdmissionWaitTimeoutRejections;
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

        long rejectionsAfter = maxQueueWaitMs == 0 ? Metrics.RpcAdmissionPredictedWaitRejections : Metrics.RpcAdmissionWaitTimeoutRejections;
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

    [TestCase(QueueLimit, TestName = "RequestQueueLimit caps the waiters")]
    [TestCase(0, TestName = "RequestQueueLimit zero lifts the cap")]
    public async Task Queued_waiters_are_capped_by_the_request_queue_limit(int requestQueueLimit)
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit(requestQueueLimit: requestQueueLimit));
        long rejectionsBefore = Metrics.RpcAdmissionPredictedWaitRejections;
        // With the EWMA unseeded every arrival predicts a zero wait, so only the cap can stop the queue from growing.
        Lease held = await Admit(gate);
        List<Task<Lease>> queued = [];
        for (int i = 0; i < QueueLimit; i++)
        {
            queued.Add(Admit(gate).AsTask());
        }
        Assert.That(gate.Queued, Is.EqualTo(QueueLimit), "waiters up to the limit must be queued");

        if (requestQueueLimit == 0)
        {
            queued.Add(Admit(gate).AsTask());
            Assert.That(gate.Queued, Is.EqualTo(QueueLimit + 1));
        }
        else
        {
            Assert.Throws<LimitExceededException>(() => Admit(gate), "the waiter over the limit must be shed synchronously");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Metrics.RpcAdmissionPredictedWaitRejections, Is.GreaterThan(rejectionsBefore));
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

    // The prediction is zero until the first request has been served, so only the budget and the cap bound an unseeded gate.
    [TestCase(0, TestName = "Uncapped queue")]
    [TestCase(QueueLimit, TestName = "Capped queue")]
    public async Task Unseeded_gate_expires_every_waiter_at_the_budget(int requestQueueLimit)
    {
        EvmAdmissionGate gate = CreateGate(SinglePermit(requestQueueLimit: requestQueueLimit));
        using Lease held = await Admit(gate);
        List<Task<Lease>> queued = [];
        for (int i = 0; i < QueueLimit; i++)
        {
            queued.Add(Admit(gate).AsTask());
        }

        if (requestQueueLimit == 0)
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

    [TestCase(1)]
    [TestCase(4)]
    public async Task Service_time_ewma_is_normalised_by_weight(int weight)
    {
        const int holdMs = 40;

        using (await Admit(weight))
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(holdMs));
        }

        Assert.That(_gate.ServiceTimeMs, Is.EqualTo((double)holdMs / weight));
    }

    [TestCase(true, TestName = "Disposing folds one ~0 ms observation in, landing near 900 at alpha 0.1")]
    [TestCase(false, TestName = "Releasing without sampling leaves the estimate untouched")]
    public async Task Service_time_ewma_moves_only_on_sampled_releases(bool sampled)
    {
        _gate.SetServiceTimeMs(1_000);

        Lease lease = await Admit();
        if (sampled)
        {
            lease.Dispose();
        }
        else
        {
            lease.ReleaseWithoutSampling();
        }

        Constraint serviceTime = sampled ? Is.LessThan(1_000).And.GreaterThan(800) : Is.EqualTo(1_000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.InFlight, Is.EqualTo(0));
            Assert.That(_gate.ServiceTimeMs, serviceTime);
        }
    }

    [TestCase(false, TestName = "Idle gate: the second release is dropped at once")]
    [TestCase(true, TestName = "Loaded gate: the second release passes for the live lease's, whose own release is dropped")]
    public async Task Surplus_release_is_dropped_and_does_not_add_a_permit(bool otherInFlight)
    {
        Lease other = otherInFlight ? await Admit() : default;
        Lease lease = await Admit();
        lease.Dispose();
        lease.Dispose();
        Assert.That(_gate.InFlight, Is.EqualTo(0));

        other.Dispose();
        Assert.That(_gate.InFlight, Is.EqualTo(0));

        Lease[] held = [await Admit(), await Admit()];
        Task<Lease> overCapacity = Admit().AsTask();
        Assert.That(overCapacity.IsCompleted, Is.False, "the surplus release must not have added a permit");

        held[0].Dispose();
        held[1].Dispose();
        (await overCapacity.WaitAsync(WaitBudget)).Dispose();
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
            Assert.That(Metrics.RpcAdmissionServiceTimeMs, Is.EqualTo(_gate.ServiceTimeMs));
        }
    }

    // Both permits are released at once, over and over; a gauge published from a stale snapshot would read 1 at rest.
    [Test]
    [NonParallelizable]
    public async Task In_flight_gauge_reads_zero_at_rest_after_concurrent_releases()
    {
        const int iterations = 20_000;
        using Barrier releaseTogether = new(EvmPermits);
        void ReleaseInStep(Lease lease)
        {
            releaseTogether.SignalAndWait();
            lease.Dispose();
        }

        for (int i = 0; i < iterations; i++)
        {
            Lease first = await Admit();
            Lease second = await Admit();
            await Task.WhenAll(Task.Run(() => ReleaseInStep(first)), Task.Run(() => ReleaseInStep(second)));
            Assert.That(Metrics.RpcAdmissionInFlight, Is.EqualTo(0), $"iteration {i}");
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

    // EWMA = budget / 4 per unit with two permits; a weight-8 and a weight-4 waiter are queued when the request under test arrives.
    [TestCase(1, false, TestName = "Weight 1 overtakes both waiters: 0 x budget / 8 is admitted")]
    [TestCase(4, false, TestName = "Weight 4 queues behind the weight-4 waiter only: 4 x budget / 8 is admitted")]
    [TestCase(8, true, TestName = "Weight 8 queues behind both: (4 + 8) x budget / 8 is shed")]
    public async Task Predicted_wait_counts_only_the_queued_work_a_request_cannot_overtake(int weight, bool shed)
    {
        _gate.SetServiceTimeMs(MaxQueueWaitMs / 4.0);
        Lease[] held = [await Admit(), await Admit()];
        Task<Lease> heavy = Admit(MaxWeight).AsTask();
        Task<Lease> medium = Admit(4).AsTask();
        List<Task<Lease>> serviceOrder = [medium, heavy];

        if (shed)
        {
            Assert.Throws<LimitExceededException>(() => Admit(weight));
        }
        else
        {
            Task<Lease> admitted = Admit(weight).AsTask();
            Assert.That(admitted.IsCompleted, Is.False, "an admitted request waits for a permit");
            // Lightest first, FIFO within a weight: a request of the medium weight is served after the medium waiter.
            serviceOrder.Insert(weight < 4 ? 0 : 1, admitted);
        }

        Assert.That(_gate.Queued, Is.EqualTo(serviceOrder.Count));
        held[0].Dispose();
        held[1].Dispose();
        // Drained in service order, so each disposed lease frees the permit the next waiter is granted.
        foreach (Task<Lease> admission in serviceOrder)
        {
            (await admission.WaitAsync(WaitBudget)).Dispose();
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
                holder.ReleaseWithoutSampling();
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
        EvmAdmissionGate gate = CreateGate(new JsonRpcConfig { EvmExecutionConcurrency = EvmPermits, EthModuleConcurrentInstances = EvmPermits, MaxQueueWaitMs = 1 }, TimeProvider.System);
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
                    using (await gate.AdmitAsync(weight, CancellationToken.None))
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
            // Deliberately not asserting which shed path ran: with a 1 ms budget the byte-weighted prediction can reject
            // every waiter up front, so no queue timeout need fire at all.
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

    private static JsonRpcConfig SinglePermit(int maxQueueWaitMs = MaxQueueWaitMs, int requestQueueLimit = 500) => new()
    {
        EvmExecutionConcurrency = 1,
        EthModuleConcurrentInstances = 1,
        MaxQueueWaitMs = maxQueueWaitMs,
        RequestQueueLimit = requestQueueLimit,
    };

    private ValueTask<Lease> Admit(int weight = MinWeight) => Admit(_gate, weight);

    private static ValueTask<Lease> Admit(EvmAdmissionGate gate, int weight = MinWeight, CancellationToken cancellationToken = default) =>
        gate.AdmitAsync(weight, cancellationToken);

    private static ResolvedMethodInfo Resolve(Type moduleType, string methodName) =>
        new(moduleType.Name, moduleType.GetMethod(methodName)!, readOnly: true, RpcEndpoint.All);

    // Records every re-arm so the arming rules the sweep relies on can be asserted; ManualTimeProvider ignores Change.
    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _dueTimes = [];
        private long _ticks;
        private TimerCallback? _callback;
        private object? _state;

        public IReadOnlyList<TimeSpan> DueTimes => _dueTimes;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;

        public void FireTimer() => _callback!(_state);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new RecordingTimer(_dueTimes);
        }

        private sealed class RecordingTimer(List<TimeSpan> dueTimes) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                dueTimes.Add(dueTime);
                return true;
            }

            public void Dispose() { }

            public ValueTask DisposeAsync() => default;
        }
    }
}
