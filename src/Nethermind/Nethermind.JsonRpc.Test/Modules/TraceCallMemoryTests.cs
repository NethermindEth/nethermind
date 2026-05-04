// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Explicit("Manual #5197 trace_call memory measurement. Set NETHERMIND_TRACE_CALL_MEMORY_ITERATIONS to control loop length.")]
public class TraceCallMemoryTests
{
    private const int DefaultIterations = 1_000;
    private static readonly Address TraceCallTarget = new("0xc200000000000000000000000000000000000000");
    private const int HeavyOutputWords = 16;
    private const int HeavyOutputBytes = HeavyOutputWords * 32;
    private const int HeavyStorageWrites = 8;

    [TestCase(TraceCallMemoryScenario.Simple, "trace")]
    [TestCase(TraceCallMemoryScenario.Simple, "stateDiff")]
    [TestCase(TraceCallMemoryScenario.Simple, "vmTrace")]
    [TestCase(TraceCallMemoryScenario.Heavy, "trace")]
    [TestCase(TraceCallMemoryScenario.Heavy, "stateDiff")]
    [TestCase(TraceCallMemoryScenario.Heavy, "vmTrace")]
    public async Task Trace_call_memory_measurement(TraceCallMemoryScenario scenario, string traceType)
    {
        int iterations = ReadPositiveInt("NETHERMIND_TRACE_CALL_MEMORY_ITERATIONS", DefaultIterations);
        int sampleEvery = ReadPositiveInt("NETHERMIND_TRACE_CALL_MEMORY_SAMPLE_EVERY", Math.Max(1, iterations / 10));

        TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await blockchain.AddFunds(TestItem.AddressB, 1000.Ether);

        ITraceRpcModule traceRpcModule = blockchain.TraceRpcModule;
        TraceCallScenarioConfiguration scenarioConfiguration = CreateScenarioConfiguration(scenario);
        string[] traceTypes = [traceType];
        EthereumJsonSerializer serializer = new();
        string outputPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"trace-call-memory-{scenario.ToString().ToLowerInvariant()}-{traceType}.csv");
        File.Delete(outputPath);

        WriteHeader(outputPath, scenario, traceType, iterations, sampleEvery);
        WriteSample(outputPath, "baseline", scenario, traceType, TraceCallResult.Empty, CaptureMemory(0, 0, forceFullGc: true));

        long totalResponseBytes = 0;
        for (int i = 1; i <= iterations; i++)
        {
            TraceCallResult traceCallResult = await TraceCallAndSerialize(
                traceRpcModule,
                scenarioConfiguration.Call,
                traceTypes,
                serializer,
                scenarioConfiguration.StateOverride);
            totalResponseBytes += traceCallResult.ResponseBytes;

            if (i % sampleEvery == 0 || i == iterations)
            {
                WriteSample(
                    outputPath,
                    i.ToString(),
                    scenario,
                    traceType,
                    traceCallResult,
                    CaptureMemory(traceCallResult.ResponseBytes, totalResponseBytes, forceFullGc: true));
            }
        }

        TestContext.AddTestAttachment(outputPath, $"trace_call memory measurement for {scenario} {traceType}");
        Assert.That(totalResponseBytes, Is.GreaterThan(0));
    }

    private static TraceCallScenarioConfiguration CreateScenarioConfiguration(TraceCallMemoryScenario scenario) =>
        scenario switch
        {
            TraceCallMemoryScenario.Simple => CreateSimpleScenarioConfiguration(),
            TraceCallMemoryScenario.Heavy => CreateHeavyScenarioConfiguration(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

    private static TraceCallScenarioConfiguration CreateSimpleScenarioConfiguration() =>
        new(
            new LegacyTransactionForRpc
            {
                From = TestItem.AddressB,
                To = TraceCallTarget,
                Gas = 100_000
            },
            new Dictionary<Address, AccountOverride>
            {
                [TraceCallTarget] = new() { Code = Bytes.FromHexString("5a60005260206000f3") }
            });

    private static TraceCallScenarioConfiguration CreateHeavyScenarioConfiguration() =>
        new(
            new LegacyTransactionForRpc
            {
                From = TestItem.AddressB,
                To = TraceCallTarget,
                Gas = 3_000_000
            },
            new Dictionary<Address, AccountOverride>
            {
                [TraceCallTarget] = new() { Code = CreateHeavyRuntimeCode() }
            });

    private static byte[] CreateHeavyRuntimeCode() =>
        Prepare.EvmCode
            .For(HeavyStorageWrites, static (prepare, i) => prepare
                .PushData(i + 1)
                .PushData(i)
                .Op(Instruction.SSTORE))
            .For(HeavyOutputWords, static (prepare, i) => prepare
                .PushData(i + 1)
                .PushData(i * 32)
                .Op(Instruction.MSTORE))
            .RETURN(0, (UInt256)HeavyOutputBytes)
            .Done;

    private static async Task<TraceCallResult> TraceCallAndSerialize(
        ITraceRpcModule traceRpcModule,
        TransactionForRpc call,
        string[] traceTypes,
        EthereumJsonSerializer serializer,
        Dictionary<Address, AccountOverride>? stateOverride)
    {
        using ResultWrapper<ParityTxTraceFromReplay> result = traceRpcModule.trace_call(call, traceTypes, stateOverride: stateOverride);
        using JsonRpcSuccessResponse response = new()
        {
            Result = result.Data,
            Id = 67,
            MethodName = "trace_call"
        };

        await using MemoryStream stream = new();
        long responseBytes = await serializer.SerializeAsync(stream, response, CancellationToken.None);
        return CaptureTraceCallResult(result.Data, responseBytes);
    }

    private static TraceCallResult CaptureTraceCallResult(ParityTxTraceFromReplay trace, long responseBytes) =>
        new(
            responseBytes,
            trace.Output?.Length ?? 0,
            CountTraceNodes(trace.Action),
            GetMaxTraceDepth(trace.Action),
            trace.StateChanges?.Count ?? 0,
            CountStateStorageSlots(trace.StateChanges),
            CountVmOperations(trace.VmTrace));

    private static int CountTraceNodes(ParityTraceAction? action) =>
        action is null ? 0 : 1 + action.Subtraces.Sum(CountTraceNodes);

    private static int GetMaxTraceDepth(ParityTraceAction? action) =>
        action is null ? 0 : action.Subtraces.Count == 0 ? 1 : 1 + action.Subtraces.Max(GetMaxTraceDepth);

    private static int CountStateStorageSlots(Dictionary<Address, ParityAccountStateChange>? stateChanges) =>
        stateChanges?.Values.Sum(static stateChange => stateChange.Storage?.Count ?? 0) ?? 0;

    private static int CountVmOperations(ParityVmTrace? vmTrace) =>
        vmTrace?.Operations.Sum(static operation => 1 + CountVmOperations(operation.Sub)) ?? 0;

    private static MemorySample CaptureMemory(long responseBytes, long totalResponseBytes, bool forceFullGc)
    {
        long managedHeapBytes = GC.GetTotalMemory(forceFullGc);
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();

        using Process process = Process.GetCurrentProcess();
        process.Refresh();

        return new MemorySample(
            responseBytes,
            totalResponseBytes,
            managedHeapBytes,
            gcInfo.HeapSizeBytes,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    private static int ReadPositiveInt(string variableName, int defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static void WriteHeader(
        string outputPath,
        TraceCallMemoryScenario scenario,
        string traceType,
        int iterations,
        int sampleEvery)
    {
        WriteLine(outputPath, "sample,scenario,traceType,iterations,sampleEvery,responseBytes,outputBytes,traceNodes,maxTraceDepth,stateAccounts,stateStorageSlots,vmOperations,totalResponseMiB,managedHeapMiB,gcHeapMiB,workingSetMiB,privateMemoryMiB,gen0,gen1,gen2");
        WriteLine(outputPath, $"config,{scenario.ToString().ToLowerInvariant()},{traceType},{iterations},{sampleEvery},0,0,0,0,0,0,0,0,0,0,0,0,0,0,0");
    }

    private static void WriteSample(
        string outputPath,
        string sample,
        TraceCallMemoryScenario scenario,
        string traceType,
        TraceCallResult traceCallResult,
        MemorySample memory) =>
        WriteLine(
            outputPath,
            $"{sample},{scenario.ToString().ToLowerInvariant()},{traceType},,,{traceCallResult.ResponseBytes},{traceCallResult.OutputBytes},{traceCallResult.TraceNodeCount},{traceCallResult.MaxTraceDepth},{traceCallResult.StateAccountCount},{traceCallResult.StateStorageSlotCount},{traceCallResult.VmOperationCount},{ToMiB(memory.TotalResponseBytes):F2},{ToMiB(memory.ManagedHeapBytes):F2},{ToMiB(memory.GcHeapBytes):F2},{ToMiB(memory.WorkingSetBytes):F2},{ToMiB(memory.PrivateMemoryBytes):F2},{memory.Gen0Collections},{memory.Gen1Collections},{memory.Gen2Collections}");

    private static void WriteLine(string outputPath, string line)
    {
        TestContext.Out.WriteLine(line);
        TestContext.Progress.WriteLine(line);
        File.AppendAllText(outputPath, line + Environment.NewLine);
    }

    private static double ToMiB(long bytes) => bytes / 1024d / 1024d;

    public enum TraceCallMemoryScenario
    {
        Simple,
        Heavy
    }

    private readonly record struct TraceCallResult(
        long ResponseBytes,
        int OutputBytes,
        int TraceNodeCount,
        int MaxTraceDepth,
        int StateAccountCount,
        int StateStorageSlotCount,
        int VmOperationCount)
    {
        public static readonly TraceCallResult Empty = new(0, 0, 0, 0, 0, 0, 0);
    }

    private readonly record struct TraceCallScenarioConfiguration(
        TransactionForRpc Call,
        Dictionary<Address, AccountOverride>? StateOverride);

    private readonly record struct MemorySample(
        long ResponseBytes,
        long TotalResponseBytes,
        long ManagedHeapBytes,
        long GcHeapBytes,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);
}
