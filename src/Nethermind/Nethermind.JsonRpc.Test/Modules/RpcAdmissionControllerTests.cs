// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class RpcAdmissionControllerTests
{
    private const int EvmPermits = 2;
    private const int MaxQueueWaitMs = 5_000;
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    [ThreadStatic]
    private static bool _releasingPermit;

    private RpcAdmissionController _controller = null!;

    [SetUp]
    public void SetUp() => _controller = new RpcAdmissionController(new JsonRpcConfig
    {
        EvmExecutionConcurrency = EvmPermits,
        TracingConcurrency = 1,
        ProofConcurrency = 1,
        MaxQueueWaitMs = MaxQueueWaitMs,
    }, LimboLogs.Instance);

    [TestCase("eth_call", RpcMethodCostClass.EvmExecution)]
    [TestCase("eth_estimateGas", RpcMethodCostClass.EvmExecution)]
    [TestCase("eth_createAccessList", RpcMethodCostClass.EvmExecution)]
    [TestCase("eth_simulateV1", RpcMethodCostClass.EvmExecution)]
    [TestCase("eth_fillTransaction", RpcMethodCostClass.EvmExecution)]
    [TestCase("debug_traceTransaction", RpcMethodCostClass.Tracing)]
    [TestCase("debug_traceCall", RpcMethodCostClass.Tracing)]
    [TestCase("debug_traceBlockByNumber", RpcMethodCostClass.Tracing)]
    [TestCase("trace_call", RpcMethodCostClass.Tracing)]
    [TestCase("trace_replayBlockTransactions", RpcMethodCostClass.Tracing)]
    [TestCase("trace_filter", RpcMethodCostClass.Tracing)]
    [TestCase("debug_simulateV1", RpcMethodCostClass.EvmExecution)]
    [TestCase("debug_intermediateRoots", RpcMethodCostClass.Tracing)]
    [TestCase("debug_standardTraceBlockToFile", RpcMethodCostClass.Tracing)]
    [TestCase("debug_standardTraceBadBlockToFile", RpcMethodCostClass.Tracing)]
    [TestCase("debug_executionWitness", RpcMethodCostClass.Tracing)]
    [TestCase("proof_call", RpcMethodCostClass.Proof)]
    [TestCase("proof_getTransactionReceipt", RpcMethodCostClass.Proof)]
    [TestCase("eth_getProof", RpcMethodCostClass.Proof)]
    [TestCase("eth_blockNumber", RpcMethodCostClass.Default)]
    [TestCase("eth_getBalance", RpcMethodCostClass.Default)]
    [TestCase("eth_getLogs", RpcMethodCostClass.Default)]
    [TestCase("eth_sendRawTransaction", RpcMethodCostClass.Default)]
    [TestCase("debug_getRawBlock", RpcMethodCostClass.Default)]
    [TestCase("engine_newPayloadV4", RpcMethodCostClass.Default)]
    [TestCase("net_version", RpcMethodCostClass.Default)]
    public void Classifies_methods_by_cost(string methodName, RpcMethodCostClass expected) =>
        Assert.That(RpcMethodCostClassifier.Classify(methodName), Is.EqualTo(expected));

    // A null expectation stands for Environment.ProcessorCount, which is not a compile-time constant.
    [TestCase(null, null, null, TestName = "Evm: processor count")]
    [TestCase(null, 6, 6, TestName = "Evm: falls back to EthModuleConcurrentInstances")]
    [TestCase(4, 6, 4, TestName = "Evm: explicit value wins")]
    [TestCase(0, 6, 1, TestName = "Evm: zero is clamped to one")]
    [TestCase(-3, null, 1, TestName = "Evm: negative is clamped to one")]
    public void Evm_execution_concurrency_default_chain(int? evmExecutionConcurrency, int? ethModuleConcurrentInstances, int? expected)
    {
        JsonRpcConfig config = new() { EvmExecutionConcurrency = evmExecutionConcurrency, EthModuleConcurrentInstances = ethModuleConcurrentInstances };

        Assert.That(config.GetEvmExecutionConcurrency(), Is.EqualTo(expected ?? Environment.ProcessorCount));
    }

    [TestCase(null, null, TestName = "Tracing: processor count minus two, clamped")]
    [TestCase(5, 5, TestName = "Tracing: explicit value wins")]
    [TestCase(0, 1, TestName = "Tracing: zero is clamped to one")]
    [TestCase(64, 64, TestName = "Tracing: an explicit value is not capped")]
    public void Tracing_concurrency_default_chain(int? tracingConcurrency, int? expected)
    {
        JsonRpcConfig config = new() { TracingConcurrency = tracingConcurrency };

        Assert.That(config.GetTracingConcurrency(), Is.EqualTo(expected ?? RpcConcurrencyLimits.ClampDerived(Environment.ProcessorCount - 2)));
    }

    [TestCase(null, null, TestName = "Proof: half the processor count, clamped")]
    [TestCase(3, 3, TestName = "Proof: explicit value wins")]
    [TestCase(-1, 1, TestName = "Proof: negative is clamped to one")]
    public void Proof_concurrency_default_chain(int? proofConcurrency, int? expected)
    {
        JsonRpcConfig config = new() { ProofConcurrency = proofConcurrency };

        Assert.That(config.GetProofConcurrency(), Is.EqualTo(expected ?? RpcConcurrencyLimits.ClampDerived(Environment.ProcessorCount / 2)));
    }

    // Each tracing/proof instance is a block-processing pipeline kept until shutdown, so the derived default stops at 16.
    [TestCase(-1, 2)]
    [TestCase(2, 2)]
    [TestCase(9, 9)]
    [TestCase(RpcConcurrencyLimits.MaxDerivedConcurrency, RpcConcurrencyLimits.MaxDerivedConcurrency)]
    [TestCase(126, RpcConcurrencyLimits.MaxDerivedConcurrency)]
    public void Derived_tracing_and_proof_defaults_are_clamped(int derived, int expected) =>
        Assert.That(RpcConcurrencyLimits.ClampDerived(derived), Is.EqualTo(expected));

    [TestCase(RpcMethodCostClass.EvmExecution, null, 20_000, 500, TestName = "Evm: MaxQueueWaitMs, not the request timeout")]
    [TestCase(RpcMethodCostClass.EvmExecution, -1, 20_000, 0, TestName = "Evm: negative is clamped to zero")]
    [TestCase(RpcMethodCostClass.Tracing, null, 20_000, 20_000, TestName = "Tracing: defaults to the request timeout")]
    [TestCase(RpcMethodCostClass.Tracing, null, -1, int.MaxValue, TestName = "Tracing: an infinite request timeout keeps the wait unbounded")]
    [TestCase(RpcMethodCostClass.Tracing, 750, 750, 750, TestName = "Tracing: explicit value wins")]
    [TestCase(RpcMethodCostClass.Tracing, -1, 20_000, 0, TestName = "Tracing: negative is clamped to zero")]
    [TestCase(RpcMethodCostClass.Proof, null, 20_000, 20_000, TestName = "Proof: defaults to the request timeout")]
    [TestCase(RpcMethodCostClass.Proof, null, -1, int.MaxValue, TestName = "Proof: an infinite request timeout keeps the wait unbounded")]
    [TestCase(RpcMethodCostClass.Proof, 0, 20_000, 0, TestName = "Proof: zero disables queueing")]
    public void Wait_budget_default_chain_per_class(RpcMethodCostClass costClass, int? configured, int timeout, int expected)
    {
        JsonRpcConfig config = new() { Timeout = timeout, MaxQueueWaitMs = 500 };
        switch (costClass)
        {
            case RpcMethodCostClass.EvmExecution when configured is not null:
                config.MaxQueueWaitMs = configured.Value;
                break;
            case RpcMethodCostClass.Tracing:
                config.TracingMaxQueueWaitMs = configured;
                break;
            case RpcMethodCostClass.Proof:
                config.ProofMaxQueueWaitMs = configured;
                break;
        }

        Assert.That(new RpcAdmissionController(config, LimboLogs.Instance).GetMaxQueueWaitMs(costClass), Is.EqualTo(expected));
    }

    [TestCase(null, 7, 7, TestName = "Trace module instances follow TracingConcurrency")]
    [TestCase(3, 7, 3, TestName = "Trace module instances: explicit value wins")]
    [TestCase(0, 7, 1, TestName = "Trace module instances: zero is clamped to one")]
    public void Trace_module_instances_default_to_tracing_concurrency(int? traceModuleConcurrentInstances, int tracingConcurrency, int expected)
    {
        JsonRpcConfig config = new() { TraceModuleConcurrentInstances = traceModuleConcurrentInstances, TracingConcurrency = tracingConcurrency };

        Assert.That(config.GetTraceModuleConcurrentInstances(), Is.EqualTo(expected));
    }

    [TestCase(null, 5, 5, TestName = "Proof module instances follow ProofConcurrency")]
    [TestCase(2, 5, 2, TestName = "Proof module instances: explicit value wins")]
    [TestCase(-2, 5, 1, TestName = "Proof module instances: negative is clamped to one")]
    public void Proof_module_instances_default_to_proof_concurrency(int? proofModuleConcurrentInstances, int proofConcurrency, int expected)
    {
        JsonRpcConfig config = new() { ProofModuleConcurrentInstances = proofModuleConcurrentInstances, ProofConcurrency = proofConcurrency };

        Assert.That(config.GetProofModuleConcurrentInstances(), Is.EqualTo(expected));
    }

    [TestCase(0, 1, TestName = "No params bytes")]
    [TestCase(RpcRequestWeight.BytesPerWeightUnit - 1, 1, TestName = "Just below one unit")]
    [TestCase(RpcRequestWeight.BytesPerWeightUnit, 2, TestName = "One unit")]
    [TestCase(4 * RpcRequestWeight.BytesPerWeightUnit + 17, 5, TestName = "Partial units round down")]
    [TestCase(7 * RpcRequestWeight.BytesPerWeightUnit, 8, TestName = "Upper clamp reached exactly")]
    [TestCase(int.MaxValue, 8, TestName = "Upper clamp")]
    public void Weight_grows_with_raw_params_size(int paramsUtf8Length, int expectedWeight) =>
        Assert.That(RpcRequestWeight.Estimate(Resolve<IEthRpcModule>("eth_call"), paramsUtf8Length), Is.EqualTo(expectedWeight));

    [Test]
    public void Weight_is_one_outside_the_evm_class_regardless_of_size() =>
        Assert.That(RpcRequestWeight.Estimate(Resolve<IEthRpcModule>("eth_blockNumber"), int.MaxValue), Is.EqualTo(1));

    [Test]
    public async Task Permits_are_respected_and_released_on_dispose()
    {
        RpcAdmissionController.Lease[] held = new RpcAdmissionController.Lease[EvmPermits];
        for (int i = 0; i < EvmPermits; i++)
        {
            ValueTask<RpcAdmissionController.Lease> admission = AdmitEthCall();
            Assert.That(admission.IsCompletedSuccessfully, Is.True, $"permit {i} should be granted synchronously");
            held[i] = admission.Result;
        }

        ValueTask<RpcAdmissionController.Lease> waiting = AdmitEthCall();
        await Task.Delay(100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.IsCompleted, Is.False, "one over the permit count must wait");
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(EvmPermits));
            Assert.That(_controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(1));
        }

        held[0].Dispose();
        using RpcAdmissionController.Lease admitted = await waiting.AsTask().WaitAsync(WaitBudget);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(EvmPermits));
            Assert.That(_controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
        }

        held[1].Dispose();
    }

    [Test]
    public async Task Rejects_immediately_when_predicted_wait_exceeds_budget()
    {
        long rejectionsBefore = Metrics.RpcAdmissionPredictedWaitRejections.GetValueOrDefault(RpcMethodCostClass.EvmExecution);
        // Two permits: the first waiter has nothing queued ahead and always queues, however slow the class; the second
        // one predicts a wait of EWMA / 2, so this makes it 2x the budget.
        _controller.SetServiceTimeMs(RpcMethodCostClass.EvmExecution, 4.0 * MaxQueueWaitMs);
        RpcAdmissionController.Lease[] held = [await AdmitEthCall(), await AdmitEthCall()];
        ValueTask<RpcAdmissionController.Lease> queued = AdmitEthCall();

        Stopwatch elapsed = Stopwatch.StartNew();
        Assert.Throws<LimitExceededException>(() => AdmitEthCall());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(MaxQueueWaitMs / 2), "rejection must not wait for the budget");
            Assert.That(Metrics.RpcAdmissionPredictedWaitRejections[RpcMethodCostClass.EvmExecution], Is.GreaterThan(rejectionsBefore));
        }

        held[0].Dispose();
        held[1].Dispose();
        (await queued.AsTask().WaitAsync(WaitBudget)).Dispose();
    }

    [TestCase(0, TestName = "Zero budget: shed on the calling thread, nothing queued")]
    [TestCase(100, TestName = "Positive budget: shed by the wait timeout")]
    public async Task Rejects_when_permits_never_free(int maxQueueWaitMs)
    {
        RpcAdmissionController controller = new(new JsonRpcConfig { EvmExecutionConcurrency = 1, MaxQueueWaitMs = maxQueueWaitMs }, LimboLogs.Instance);
        ResolvedMethodInfo ethCall = Resolve<IEthRpcModule>("eth_call");
        ConcurrentDictionary<RpcMethodCostClass, long> rejections = maxQueueWaitMs == 0
            ? Metrics.RpcAdmissionPredictedWaitRejections
            : Metrics.RpcAdmissionWaitTimeoutRejections;
        long rejectionsBefore = rejections.GetValueOrDefault(RpcMethodCostClass.EvmExecution);
        RpcAdmissionController.Lease held = await controller.AdmitAsync(ethCall, 0);

        if (maxQueueWaitMs == 0)
        {
            Assert.Throws<LimitExceededException>(() => controller.AdmitAsync(ethCall, 0), "a zero budget must reject synchronously, without a waiter");
        }
        else
        {
            Assert.ThrowsAsync<LimitExceededException>(async () => await controller.AdmitAsync(ethCall, 0));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejections[RpcMethodCostClass.EvmExecution], Is.GreaterThan(rejectionsBefore));
            Assert.That(controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(1));
        }

        held.Dispose();
        ValueTask<RpcAdmissionController.Lease> fresh = controller.AdmitAsync(ethCall, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fresh.IsCompletedSuccessfully, Is.True, "the freed permit must not have gone to the timed-out waiter");
            Assert.That(controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(1));
        }
        fresh.Result.Dispose();
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task Service_time_ewma_is_normalised_by_weight(int weight)
    {
        const int holdMs = 40;

        Stopwatch outer = Stopwatch.StartNew();
        using (await AdmitEthCall(weight))
        {
            Thread.Sleep(holdMs);
        }
        outer.Stop();

        double serviceTimeMs = _controller.GetServiceTimeMs(RpcMethodCostClass.EvmExecution);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceTimeMs, Is.GreaterThanOrEqualTo(holdMs * 0.9 / weight));
            Assert.That(serviceTimeMs, Is.LessThanOrEqualTo(outer.Elapsed.TotalMilliseconds / weight + 1));
        }
    }

    [TestCase(true, TestName = "Disposing folds one ~0 ms observation in, landing near 900 at alpha 0.1")]
    [TestCase(false, TestName = "Releasing without sampling leaves the estimate untouched")]
    public async Task Service_time_ewma_moves_only_on_sampled_releases(bool sampled)
    {
        _controller.SetServiceTimeMs(RpcMethodCostClass.EvmExecution, 1_000);

        RpcAdmissionController.Lease lease = await AdmitEthCall();
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
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(_controller.GetServiceTimeMs(RpcMethodCostClass.EvmExecution), serviceTime);
        }
    }

    [TestCase(false, TestName = "Idle class: the second release is dropped and counted at once")]
    [TestCase(true, TestName = "Loaded class: the second release passes for the live lease's, whose own release is dropped and counted")]
    public async Task Surplus_release_is_counted_and_the_permits_restored_once_the_class_drains(bool otherInFlight)
    {
        long anomaliesBefore = Metrics.RpcAdmissionReleaseAnomalies.GetValueOrDefault(RpcMethodCostClass.EvmExecution);
        RpcAdmissionController.Lease other = otherInFlight ? await AdmitEthCall() : default;
        RpcAdmissionController.Lease lease = await AdmitEthCall();
        lease.Dispose();
        lease.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(Metrics.RpcAdmissionReleaseAnomalies[RpcMethodCostClass.EvmExecution], Is.EqualTo(otherInFlight ? anomaliesBefore : anomaliesBefore + 1));
        }

        other.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(Metrics.RpcAdmissionReleaseAnomalies[RpcMethodCostClass.EvmExecution], Is.EqualTo(anomaliesBefore + 1));
        }

        RpcAdmissionController.Lease[] held = [await AdmitEthCall(), await AdmitEthCall()];
        ValueTask<RpcAdmissionController.Lease> overCapacity = AdmitEthCall();
        Assert.That(overCapacity.IsCompleted, Is.False, "the surplus release must not have added a permit");

        held[0].Dispose();
        held[1].Dispose();
        (await overCapacity.AsTask().WaitAsync(WaitBudget)).Dispose();
    }

    [Test]
    public void Default_class_is_never_gated()
    {
        ResolvedMethodInfo blockNumber = Resolve<IEthRpcModule>("eth_blockNumber");
        for (int i = 0; i < 1_000; i++)
        {
            ValueTask<RpcAdmissionController.Lease> admission = _controller.AdmitAsync(blockNumber, 0);
            Assert.That(admission.IsCompletedSuccessfully, Is.True);
            Assert.That(admission.Result.IsGated, Is.False);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_controller.GetPermits(RpcMethodCostClass.Default), Is.EqualTo(0));
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.Default), Is.EqualTo(0));
            Assert.That(_controller.GetQueued(RpcMethodCostClass.Default), Is.EqualTo(0));
        }
    }

    // Proves each gated class is wired to its own gate sized from its own config knob; the permit mechanics are covered above.
    [TestCase(RpcMethodCostClass.EvmExecution, EvmPermits)]
    [TestCase(RpcMethodCostClass.Tracing, 1)]
    [TestCase(RpcMethodCostClass.Proof, 1)]
    public async Task Every_gated_class_hands_out_a_permit(RpcMethodCostClass costClass, int expectedPermits)
    {
        ResolvedMethodInfo method = costClass switch
        {
            RpcMethodCostClass.EvmExecution => Resolve<IEthRpcModule>("eth_call"),
            RpcMethodCostClass.Tracing => Resolve<IDebugRpcModule>("debug_traceCall"),
            _ => Resolve<IEthRpcModule>("eth_getProof"),
        };

        using (RpcAdmissionController.Lease lease = await _controller.AdmitAsync(method, 0))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lease.IsGated, Is.True);
                Assert.That(_controller.GetPermits(costClass), Is.EqualTo(expectedPermits));
                Assert.That(_controller.GetInFlight(costClass), Is.EqualTo(1));
            }
        }

        Assert.That(_controller.GetInFlight(costClass), Is.EqualTo(0));
    }

    [Test]
    [NonParallelizable]
    public async Task Queued_and_in_flight_gauges_follow_the_gate()
    {
        RpcAdmissionController.Lease[] held = [await AdmitEthCall(), await AdmitEthCall()];
        ValueTask<RpcAdmissionController.Lease> waiting = AdmitEthCall();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.RpcAdmissionInFlight[RpcMethodCostClass.EvmExecution], Is.EqualTo(EvmPermits));
            Assert.That(Metrics.RpcAdmissionQueued[RpcMethodCostClass.EvmExecution], Is.EqualTo(1));
        }

        held[0].Dispose();
        held[1].Dispose();
        (await waiting.AsTask().WaitAsync(WaitBudget)).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.RpcAdmissionInFlight[RpcMethodCostClass.EvmExecution], Is.EqualTo(0));
            Assert.That(Metrics.RpcAdmissionQueued[RpcMethodCostClass.EvmExecution], Is.EqualTo(0));
            Assert.That(Metrics.RpcAdmissionServiceTimeMs[RpcMethodCostClass.EvmExecution], Is.EqualTo(_controller.GetServiceTimeMs(RpcMethodCostClass.EvmExecution)));
        }
    }

    // Both permits are released at once, over and over; a gauge published from a stale snapshot would read 1 at rest.
    [Test]
    [NonParallelizable]
    public async Task In_flight_gauge_reads_zero_at_rest_after_concurrent_releases()
    {
        const int Iterations = 20_000;
        using Barrier releaseTogether = new(EvmPermits);
        void ReleaseInStep(RpcAdmissionController.Lease lease)
        {
            releaseTogether.SignalAndWait();
            lease.Dispose();
        }

        for (int i = 0; i < Iterations; i++)
        {
            RpcAdmissionController.Lease first = await AdmitEthCall();
            RpcAdmissionController.Lease second = await AdmitEthCall();
            await Task.WhenAll(Task.Run(() => ReleaseInStep(first)), Task.Run(() => ReleaseInStep(second)));
            Assert.That(Metrics.RpcAdmissionInFlight[RpcMethodCostClass.EvmExecution], Is.EqualTo(0), $"iteration {i}");
        }
    }

    [Test]
    public async Task Lighter_waiters_are_served_first_and_fifo_within_a_weight()
    {
        RpcAdmissionController.Lease[] held = [await AdmitEthCall(RpcRequestWeight.MaxWeight), await AdmitEthCall(RpcRequestWeight.MaxWeight)];
        ValueTask<RpcAdmissionController.Lease> heavyFirst = AdmitEthCall(RpcRequestWeight.MaxWeight);
        ValueTask<RpcAdmissionController.Lease> heavySecond = AdmitEthCall(RpcRequestWeight.MaxWeight);
        ValueTask<RpcAdmissionController.Lease> lightFirst = AdmitEthCall(1);
        ValueTask<RpcAdmissionController.Lease> lightSecond = AdmitEthCall(1);
        ValueTask<RpcAdmissionController.Lease>[] expectedOrder = [lightFirst, lightSecond, heavyFirst, heavySecond];
        Assert.That(_controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(expectedOrder.Length));

        RpcAdmissionController.Lease releasing = held[0];
        for (int i = 0; i < expectedOrder.Length; i++)
        {
            releasing.Dispose();
            releasing = await expectedOrder[i].AsTask().WaitAsync(WaitBudget);
            for (int later = i + 1; later < expectedOrder.Length; later++)
            {
                Assert.That(expectedOrder[later].IsCompleted, Is.False, $"waiter {later} must not be admitted before waiter {i}");
            }
        }

        releasing.Dispose();
        held[1].Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
        }
    }

    // EWMA = budget / 4 per unit with two permits; a weight-8 and a weight-4 waiter are queued when the request under test arrives.
    [TestCase(1, false, TestName = "Weight 1 overtakes both waiters: 0 x budget / 8 is admitted")]
    [TestCase(4, false, TestName = "Weight 4 queues behind the weight-4 waiter only: 4 x budget / 8 is admitted")]
    [TestCase(8, true, TestName = "Weight 8 queues behind both: (4 + 8) x budget / 8 is shed")]
    public async Task Predicted_wait_counts_only_the_queued_work_a_request_cannot_overtake(int weight, bool shed)
    {
        _controller.SetServiceTimeMs(RpcMethodCostClass.EvmExecution, MaxQueueWaitMs / 4.0);
        RpcAdmissionController.Lease[] held = [await AdmitEthCall(), await AdmitEthCall()];
        ValueTask<RpcAdmissionController.Lease> heavy = AdmitEthCall(RpcRequestWeight.MaxWeight);
        ValueTask<RpcAdmissionController.Lease> medium = AdmitEthCall(4);
        List<ValueTask<RpcAdmissionController.Lease>> serviceOrder = [medium, heavy];

        if (shed)
        {
            Assert.Throws<LimitExceededException>(() => AdmitEthCall(weight));
        }
        else
        {
            ValueTask<RpcAdmissionController.Lease> admitted = AdmitEthCall(weight);
            Assert.That(admitted.IsCompleted, Is.False, "an admitted request waits for a permit");
            // Lightest first, FIFO within a weight: a request of the medium weight is served after the medium waiter.
            serviceOrder.Insert(weight < 4 ? 0 : 1, admitted);
        }

        Assert.That(_controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(serviceOrder.Count));
        held[0].Dispose();
        held[1].Dispose();
        // Drained in service order, so each disposed lease frees the permit the next waiter is granted.
        foreach (ValueTask<RpcAdmissionController.Lease> admission in serviceOrder)
        {
            (await admission.AsTask().WaitAsync(WaitBudget)).Dispose();
        }
    }

    [Test]
    public async Task Overtaken_heavy_waiter_is_shed_at_its_wait_budget_while_light_traffic_keeps_flowing()
    {
        const int budgetMs = 200;
        RpcAdmissionController controller = new(new JsonRpcConfig { EvmExecutionConcurrency = 1, MaxQueueWaitMs = budgetMs }, LimboLogs.Instance);
        ResolvedMethodInfo ethCall = Resolve<IEthRpcModule>("eth_call");
        RpcAdmissionController.Lease holder = await controller.AdmitAsync(ethCall, 0);
        Task<RpcAdmissionController.Lease> heavy = controller.AdmitAsync(ethCall, ParamsLengthForWeight(RpcRequestWeight.MaxWeight)).AsTask();

        // A light request is queued before each release, so the single permit always has a lighter taker than the heavy
        // waiter. Releasing without sampling keeps the EWMA at zero, so no light request can be shed by prediction.
        int lightServed = 0;
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!heavy.IsCompleted && elapsed.Elapsed < WaitBudget)
        {
            ValueTask<RpcAdmissionController.Lease> light = controller.AdmitAsync(ethCall, 0);
            holder.ReleaseWithoutSampling();
            holder = await light.AsTask().WaitAsync(WaitBudget);
            lightServed++;
            await Task.Delay(5);
        }
        holder.Dispose();

        Assert.ThrowsAsync<LimitExceededException>(() => heavy);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lightServed, Is.GreaterThan(1), "light requests must keep being served past the heavy waiter");
            Assert.That(elapsed.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(budgetMs * 0.9), "the heavy waiter must be shed no earlier than its budget");
            Assert.That(controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Timeouts_racing_grants_neither_leak_nor_double_release_permits()
    {
        const int requests = 2_000;
        RpcAdmissionController controller = new(new JsonRpcConfig { EvmExecutionConcurrency = EvmPermits, MaxQueueWaitMs = 1 }, LimboLogs.Instance);
        ResolvedMethodInfo ethCall = Resolve<IEthRpcModule>("eth_call");
        long timeoutsBefore = Metrics.RpcAdmissionWaitTimeoutRejections.GetValueOrDefault(RpcMethodCostClass.EvmExecution);
        int admitted = 0;
        int shed = 0;
        Task[] callers = new Task[requests];
        for (int i = 0; i < requests; i++)
        {
            int paramsUtf8Length = ParamsLengthForWeight(i % RpcRequestWeight.MaxWeight + 1);
            callers[i] = Task.Run(async () =>
            {
                try
                {
                    using (await controller.AdmitAsync(ethCall, paramsUtf8Length))
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
            Assert.That(Metrics.RpcAdmissionWaitTimeoutRejections[RpcMethodCostClass.EvmExecution], Is.GreaterThan(timeoutsBefore), "some waiters must have timed out");
            Assert.That(controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
        }

        for (int i = 0; i < EvmPermits; i++)
        {
            Assert.That(controller.AdmitAsync(ethCall, 0).IsCompletedSuccessfully, Is.True, $"permit {i} must be available again");
        }
    }

    [Test]
    public async Task Releasing_a_permit_never_runs_the_next_waiters_continuation_inline()
    {
        RpcAdmissionController.Lease[] held = [await AdmitEthCall(), await AdmitEthCall()];
        bool? continuationRanOnReleaser = null;
        Task<RpcAdmissionController.Lease> probe = AdmitEthCall().AsTask().ContinueWith(admission =>
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

    private ValueTask<RpcAdmissionController.Lease> AdmitEthCall(int weight = 1) =>
        _controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), ParamsLengthForWeight(weight));

    private static int ParamsLengthForWeight(int weight) => (weight - 1) * RpcRequestWeight.BytesPerWeightUnit;

    private static ResolvedMethodInfo Resolve<TModule>(string methodName) where TModule : IRpcModule =>
        new(typeof(TModule).Name, typeof(TModule).GetMethod(methodName)!, readOnly: true, RpcEndpoint.All);
}
