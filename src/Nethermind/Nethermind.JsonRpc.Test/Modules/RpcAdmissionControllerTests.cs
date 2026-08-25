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
        // Two permits: one queued request predicts a wait of EWMA / 2, so this makes it 2x the budget.
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
        using (await _controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), (weight - 1) * RpcRequestWeight.BytesPerWeightUnit))
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

    private ValueTask<RpcAdmissionController.Lease> AdmitEthCall() => _controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), 0);

    private static ResolvedMethodInfo Resolve<TModule>(string methodName) where TModule : IRpcModule =>
        new(typeof(TModule).Name, typeof(TModule).GetMethod(methodName)!, readOnly: true, RpcEndpoint.All);
}
