// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
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

    private RpcAdmissionController _controller = null!;

    [SetUp]
    public void SetUp() => _controller = new RpcAdmissionController(new JsonRpcConfig
    {
        EvmExecutionConcurrency = EvmPermits,
        TracingConcurrency = 1,
        ProofConcurrency = 1,
        MaxQueueWaitMs = MaxQueueWaitMs,
    });

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

    [TestCase(null, null, TestName = "Tracing: processor count minus two, at least two")]
    [TestCase(5, 5, TestName = "Tracing: explicit value wins")]
    [TestCase(0, 1, TestName = "Tracing: zero is clamped to one")]
    public void Tracing_concurrency_default_chain(int? tracingConcurrency, int? expected)
    {
        JsonRpcConfig config = new() { TracingConcurrency = tracingConcurrency };

        Assert.That(config.GetTracingConcurrency(), Is.EqualTo(expected ?? Math.Max(2, Environment.ProcessorCount - 2)));
    }

    [TestCase(null, null, TestName = "Proof: half the processor count, at least two")]
    [TestCase(3, 3, TestName = "Proof: explicit value wins")]
    [TestCase(-1, 1, TestName = "Proof: negative is clamped to one")]
    public void Proof_concurrency_default_chain(int? proofConcurrency, int? expected)
    {
        JsonRpcConfig config = new() { ProofConcurrency = proofConcurrency };

        Assert.That(config.GetProofConcurrency(), Is.EqualTo(expected ?? Math.Max(2, Environment.ProcessorCount / 2)));
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
        // Two permits: the first waiter predicts its own unit at EWMA / 2 = 0.75x the budget, the second one queued
        // behind it predicts (1 + 1) x EWMA / 2 = 1.5x the budget.
        _controller.SetServiceTimeMs(RpcMethodCostClass.EvmExecution, 1.5 * MaxQueueWaitMs);
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

    [Test]
    public async Task Rejects_after_wait_timeout_when_permits_never_free()
    {
        RpcAdmissionController controller = new(new JsonRpcConfig { EvmExecutionConcurrency = 1, MaxQueueWaitMs = 100 });
        long rejectionsBefore = Metrics.RpcAdmissionWaitTimeoutRejections.GetValueOrDefault(RpcMethodCostClass.EvmExecution);
        using RpcAdmissionController.Lease held = await controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), 0);

        Assert.ThrowsAsync<LimitExceededException>(async () => await controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), 0));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.RpcAdmissionWaitTimeoutRejections[RpcMethodCostClass.EvmExecution], Is.GreaterThan(rejectionsBefore));
            Assert.That(controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(0));
            Assert.That(controller.GetInFlight(RpcMethodCostClass.EvmExecution), Is.EqualTo(1));
        }
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

    // EWMA = budget / 4 per unit with two permits; one weight-8 waiter is queued when the request under test arrives.
    [TestCase(1, false, TestName = "Weight 1 overtakes the heavy waiter: (0 + 1) x budget / 8 is admitted")]
    [TestCase(4, false, TestName = "Weight 4 overtakes the heavy waiter: (0 + 4) x budget / 8 is admitted")]
    [TestCase(8, true, TestName = "Weight 8 queues behind it: (8 + 8) x budget / 8 is shed")]
    public async Task Predicted_wait_counts_only_the_queued_work_a_request_cannot_overtake(int weight, bool shed)
    {
        _controller.SetServiceTimeMs(RpcMethodCostClass.EvmExecution, MaxQueueWaitMs / 4.0);
        RpcAdmissionController.Lease[] held = [await AdmitEthCall(), await AdmitEthCall()];
        List<ValueTask<RpcAdmissionController.Lease>> waiting = [AdmitEthCall(RpcRequestWeight.MaxWeight)];

        if (shed)
        {
            Assert.Throws<LimitExceededException>(() => AdmitEthCall(weight));
        }
        else
        {
            waiting.Add(AdmitEthCall(weight));
            Assert.That(waiting[^1].IsCompleted, Is.False, "an admitted request waits for a permit");
        }

        Assert.That(_controller.GetQueued(RpcMethodCostClass.EvmExecution), Is.EqualTo(waiting.Count));
        held[0].Dispose();
        held[1].Dispose();
        foreach (ValueTask<RpcAdmissionController.Lease> admission in waiting)
        {
            (await admission.AsTask().WaitAsync(WaitBudget)).Dispose();
        }
    }

    [Test]
    public async Task Overtaken_heavy_waiter_is_shed_at_its_wait_budget_while_light_traffic_keeps_flowing()
    {
        const int budgetMs = 200;
        RpcAdmissionController controller = new(new JsonRpcConfig { EvmExecutionConcurrency = 1, MaxQueueWaitMs = budgetMs });
        ResolvedMethodInfo ethCall = Resolve<IEthRpcModule>("eth_call");
        RpcAdmissionController.Lease holder = await controller.AdmitAsync(ethCall, 0);
        Task<RpcAdmissionController.Lease> heavy = controller.AdmitAsync(ethCall, (RpcRequestWeight.MaxWeight - 1) * RpcRequestWeight.BytesPerWeightUnit).AsTask();

        // A light request is queued before each release, so the single permit always has a lighter taker than the heavy waiter.
        int lightServed = 0;
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!heavy.IsCompleted && elapsed.Elapsed < WaitBudget)
        {
            ValueTask<RpcAdmissionController.Lease> light = controller.AdmitAsync(ethCall, 0);
            holder.Dispose();
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
        RpcAdmissionController controller = new(new JsonRpcConfig { EvmExecutionConcurrency = EvmPermits, MaxQueueWaitMs = 1 });
        ResolvedMethodInfo ethCall = Resolve<IEthRpcModule>("eth_call");
        int admitted = 0;
        int shed = 0;
        Task[] callers = new Task[requests];
        for (int i = 0; i < requests; i++)
        {
            int paramsUtf8Length = i % RpcRequestWeight.MaxWeight * RpcRequestWeight.BytesPerWeightUnit;
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
            continuationRanOnReleaser = t_releasingPermit;
            return admission.Result;
        }, TaskContinuationOptions.ExecuteSynchronously);

        t_releasingPermit = true;
        held[0].Dispose();
        t_releasingPermit = false;

        using (await probe.WaitAsync(WaitBudget))
        {
            Assert.That(continuationRanOnReleaser, Is.False);
        }
        held[1].Dispose();
    }

    [ThreadStatic]
    private static bool t_releasingPermit;

    private ValueTask<RpcAdmissionController.Lease> AdmitEthCall(int weight = 1) =>
        _controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), (weight - 1) * RpcRequestWeight.BytesPerWeightUnit);

    private static ResolvedMethodInfo Resolve<TModule>(string methodName) where TModule : IRpcModule =>
        new(typeof(TModule).Name, typeof(TModule).GetMethod(methodName)!, readOnly: true, RpcEndpoint.All);
}
