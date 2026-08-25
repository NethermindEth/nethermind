// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;
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

    [TearDown]
    public void TearDown() => _controller.Dispose();

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

    [TestCase(0, 0, 1, TestName = "No overrides")]
    [TestCase(64 * 1024 - 1, 0, 1, TestName = "Just below one unit of code")]
    [TestCase(64 * 1024, 0, 2, TestName = "One unit of code")]
    [TestCase(0, 1024, 2, TestName = "One unit of storage slots")]
    [TestCase(3 * 64 * 1024, 1024, 5, TestName = "Code and slots add up")]
    [TestCase(7 * 64 * 1024, 0, 8, TestName = "Upper clamp reached exactly")]
    [TestCase(100 * 64 * 1024, 4096, 8, TestName = "Upper clamp")]
    public void Weight_grows_with_state_override_size(int codeLength, int slotCount, int expectedWeight)
    {
        Dictionary<Address, AccountOverride> stateOverride = BuildStateOverride(codeLength, slotCount);
        object?[] parameters = [new LegacyTransactionForRpc(), null, stateOverride, null];

        Assert.That(RpcRequestWeight.Estimate(Resolve<IEthRpcModule>("eth_call"), parameters, parameters.Length), Is.EqualTo(expectedWeight));
    }

    [Test]
    public void Weight_counts_simulate_block_overrides()
    {
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls =
            [
                new BlockStateCall<TransactionForRpc> { StateOverrides = BuildStateOverride(64 * 1024, 0) },
                new BlockStateCall<TransactionForRpc> { StateOverrides = BuildStateOverride(64 * 1024, 0) },
            ],
        };
        object?[] parameters = [payload, null];

        Assert.That(RpcRequestWeight.Estimate(Resolve<IEthRpcModule>("eth_simulateV1"), parameters, parameters.Length), Is.EqualTo(3));
    }

    [Test]
    public void Weight_is_one_outside_the_evm_class_even_with_overrides()
    {
        object?[] parameters = [BuildStateOverride(100 * 64 * 1024, 0)];

        Assert.That(RpcRequestWeight.Estimate(Resolve<IEthRpcModule>("eth_blockNumber"), parameters, parameters.Length), Is.EqualTo(1));
    }

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
        using RpcAdmissionController controller = new(new JsonRpcConfig { EvmExecutionConcurrency = 1, MaxQueueWaitMs = 100 });
        long rejectionsBefore = Metrics.RpcAdmissionWaitTimeoutRejections.GetValueOrDefault(RpcMethodCostClass.EvmExecution);
        using RpcAdmissionController.Lease held = await controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), null, 0);

        Assert.ThrowsAsync<LimitExceededException>(async () => await controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), null, 0));
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
        object?[] parameters = [new LegacyTransactionForRpc(), null, BuildStateOverride((weight - 1) * 64 * 1024, 0), null];

        Stopwatch outer = Stopwatch.StartNew();
        using (await _controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), parameters, parameters.Length))
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

    [Test]
    public async Task Service_time_ewma_moves_towards_new_observations()
    {
        _controller.SetServiceTimeMs(RpcMethodCostClass.EvmExecution, 1_000);

        using (await AdmitEthCall())
        {
        }

        double serviceTimeMs = _controller.GetServiceTimeMs(RpcMethodCostClass.EvmExecution);
        Assert.That(serviceTimeMs, Is.LessThan(1_000).And.GreaterThan(800), "one ~0 ms observation at alpha 0.1 lands near 900");
    }

    [Test]
    public void Default_class_is_never_gated()
    {
        ResolvedMethodInfo blockNumber = Resolve<IEthRpcModule>("eth_blockNumber");
        for (int i = 0; i < 1_000; i++)
        {
            ValueTask<RpcAdmissionController.Lease> admission = _controller.AdmitAsync(blockNumber, null, 0);
            Assert.That(admission.IsCompletedSuccessfully, Is.True);
            Assert.That(admission.Result.WorkerPool, Is.Null);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_controller.GetPermits(RpcMethodCostClass.Default), Is.EqualTo(0));
            Assert.That(_controller.GetInFlight(RpcMethodCostClass.Default), Is.EqualTo(0));
            Assert.That(_controller.GetQueued(RpcMethodCostClass.Default), Is.EqualTo(0));
        }
    }

    [TestCase(RpcMethodCostClass.EvmExecution, true)]
    [TestCase(RpcMethodCostClass.Tracing, true)]
    [TestCase(RpcMethodCostClass.Proof, false)]
    public async Task Only_evm_and_tracing_classes_dispatch_to_a_worker_pool(RpcMethodCostClass costClass, bool expectsPool)
    {
        ResolvedMethodInfo method = costClass switch
        {
            RpcMethodCostClass.EvmExecution => Resolve<IEthRpcModule>("eth_call"),
            RpcMethodCostClass.Tracing => Resolve<IDebugRpcModule>("debug_traceCall"),
            _ => Resolve<IEthRpcModule>("eth_getProof"),
        };

        using RpcAdmissionController.Lease lease = await _controller.AdmitAsync(method, null, 0);

        Assert.That(lease.WorkerPool, expectsPool ? Is.Not.Null : Is.Null);
        if (expectsPool)
        {
            Assert.That(lease.WorkerPool!.WorkerCount, Is.EqualTo(_controller.GetPermits(costClass)));
        }
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

    private ValueTask<RpcAdmissionController.Lease> AdmitEthCall() => _controller.AdmitAsync(Resolve<IEthRpcModule>("eth_call"), null, 0);

    private static ResolvedMethodInfo Resolve<TModule>(string methodName) where TModule : IRpcModule =>
        new(typeof(TModule).Name, typeof(TModule).GetMethod(methodName)!, readOnly: true, RpcEndpoint.All);

    private static Dictionary<Address, AccountOverride> BuildStateOverride(int codeLength, int slotCount)
    {
        Dictionary<UInt256, Core.Crypto.Hash256> slots = new(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            slots[(UInt256)i] = TestItem.KeccakA;
        }

        return new Dictionary<Address, AccountOverride>
        {
            [TestItem.AddressA] = new AccountOverride { Code = new byte[codeLength], State = slots },
        };
    }
}
