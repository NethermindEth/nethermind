// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay gas-benchmark payload files through the full newPayload path:
/// JSON deserialization → ExecutionPayloadV3 → TryGetBlock → BranchProcessor.Process.
/// Each iteration re-deserializes JSON from memory, measuring the overhead of payload
/// decoding, RLP transaction decoding, and block construction on top of block processing.
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasNewPayloadBenchmarks
{
    private IWorldState _state;
    private BranchProcessor _branchProcessor;
    private BlockHeader _preBlockHeader;
    private byte[] _rawJsonBytes;
    private static readonly JsonSerializerOptions s_jsonOptions = EthereumJsonSerializer.JsonOptions;
    private static readonly EthereumEcdsa s_ecdsa = new(1);

    // Timing breakdown accumulators (printed in GlobalCleanup)
    private long _jsonParseMs;
    private long _payloadDeserializeMs;
    private long _tryGetBlockMs;
    private long _senderRecoveryMs;
    private long _blockProcessMs;
    private int _iterationCount;

    [ParamsSource(nameof(GetTestCases))]
    public GasPayloadBenchmarks.TestCase Scenario { get; set; }

    public static IEnumerable<GasPayloadBenchmarks.TestCase> GetTestCases() => GasPayloadBenchmarks.GetTestCases();

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);

        PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, pragueSpec);

        _state = PayloadLoader.CreateWorldState();
        _preBlockHeader = BlockBenchmarkHelper.CreateGenesisHeader();

        TestBlockhashProvider blockhashProvider = new();
        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            _state, blockhashProvider, specProvider);

        BlockBenchmarkHelper.ExecuteSetupPayload(_state, txProcessor, _preBlockHeader, Scenario, pragueSpec);

        BlockProcessor blockProcessor = BlockBenchmarkHelper.CreateBlockProcessor(
            specProvider, txProcessor, _state);

        _branchProcessor = new BranchProcessor(
            blockProcessor, specProvider, _state,
            new BeaconBlockRootHandler(txProcessor, _state),
            blockhashProvider, LimboLogs.Instance, preWarmer: null);

        // Store raw JSON bytes for deserialization in each iteration
        string rawJson = PayloadLoader.ReadRawJson(Scenario.FilePath);
        _rawJsonBytes = System.Text.Encoding.UTF8.GetBytes(rawJson);

        // Warm up: deserialize + process once, then verify correctness
        Block block = DeserializeAndBuildBlock();
        Block[] result = _branchProcessor.Process(
            _preBlockHeader, [block],
            ProcessingOptions.NoValidation | ProcessingOptions.ForceProcessing,
            NullBlockTracer.Instance);
        PayloadLoader.VerifyProcessedBlock(result[0], Scenario.ToString(), Scenario.FilePath);
    }

    [Benchmark]
    public void ProcessBlock()
    {
        Stopwatch sw = Stopwatch.StartNew();

        Block block = DeserializeAndBuildBlock(sw);

        sw.Restart();
        _branchProcessor.Process(
            _preBlockHeader, [block],
            ProcessingOptions.NoValidation | ProcessingOptions.ForceProcessing,
            NullBlockTracer.Instance);
        _blockProcessMs += sw.ElapsedMilliseconds;
        _iterationCount++;
    }

    /// <summary>
    /// Deserializes the raw JSON-RPC payload into an ExecutionPayloadV3, extracts additional
    /// parameters (parentBeaconBlockRoot, executionRequests), and converts to a Block via TryGetBlock.
    /// </summary>
    private Block DeserializeAndBuildBlock(Stopwatch sw = null)
    {
        sw?.Restart();
        using JsonDocument doc = JsonDocument.Parse(_rawJsonBytes);
        JsonElement paramsArray = doc.RootElement.GetProperty("params");
        if (sw is not null) _jsonParseMs += sw.ElapsedMilliseconds;

        // Deserialize ExecutionPayloadV3 from the first parameter
        sw?.Restart();
        ExecutionPayloadV3 payload = JsonSerializer.Deserialize<ExecutionPayloadV3>(
            paramsArray[0].GetRawText(), s_jsonOptions);

        // Extract parentBeaconBlockRoot (params[2]) — separate JSON-RPC parameter
        if (paramsArray.GetArrayLength() > 2 && paramsArray[2].ValueKind == JsonValueKind.String)
        {
            payload.ParentBeaconBlockRoot = JsonSerializer.Deserialize<Hash256>(
                paramsArray[2].GetRawText(), s_jsonOptions);
        }

        // Extract executionRequests (params[3]) — Prague V4 parameter
        if (paramsArray.GetArrayLength() > 3 && paramsArray[3].ValueKind == JsonValueKind.Array)
        {
            payload.ExecutionRequests = JsonSerializer.Deserialize<byte[][]>(
                paramsArray[3].GetRawText(), s_jsonOptions);
        }
        if (sw is not null) _payloadDeserializeMs += sw.ElapsedMilliseconds;

        sw?.Restart();
        BlockDecodingResult decodingResult = payload.TryGetBlock();
        if (decodingResult.Block is null)
        {
            throw new InvalidOperationException(
                $"Failed to decode block from payload: {decodingResult.Error}");
        }
        if (sw is not null) _tryGetBlockMs += sw.ElapsedMilliseconds;

        // Recover sender addresses — in production this happens during block validation
        sw?.Restart();
        Block block = decodingResult.Block;
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            block.Transactions[i].SenderAddress = s_ecdsa.RecoverAddress(block.Transactions[i]);
        }
        if (sw is not null) _senderRecoveryMs += sw.ElapsedMilliseconds;

        return block;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_iterationCount > 0)
        {
            long totalMs = _jsonParseMs + _payloadDeserializeMs + _tryGetBlockMs + _senderRecoveryMs + _blockProcessMs;
            Console.WriteLine();
            Console.WriteLine($"--- NewPayload timing breakdown ({Scenario}, {_iterationCount} iterations) ---");
            Console.WriteLine($"  JSON parse:           {_jsonParseMs,6} ms  ({100.0 * _jsonParseMs / totalMs:F1}%)  avg {1.0 * _jsonParseMs / _iterationCount:F1} ms/iter");
            Console.WriteLine($"  Payload deserialize:  {_payloadDeserializeMs,6} ms  ({100.0 * _payloadDeserializeMs / totalMs:F1}%)  avg {1.0 * _payloadDeserializeMs / _iterationCount:F1} ms/iter");
            Console.WriteLine($"  TryGetBlock:          {_tryGetBlockMs,6} ms  ({100.0 * _tryGetBlockMs / totalMs:F1}%)  avg {1.0 * _tryGetBlockMs / _iterationCount:F1} ms/iter");
            Console.WriteLine($"  Sender recovery:      {_senderRecoveryMs,6} ms  ({100.0 * _senderRecoveryMs / totalMs:F1}%)  avg {1.0 * _senderRecoveryMs / _iterationCount:F1} ms/iter");
            Console.WriteLine($"  Block processing:     {_blockProcessMs,6} ms  ({100.0 * _blockProcessMs / totalMs:F1}%)  avg {1.0 * _blockProcessMs / _iterationCount:F1} ms/iter");
            Console.WriteLine($"  Total:                {totalMs,6} ms         avg {1.0 * totalMs / _iterationCount:F1} ms/iter");
        }

        _state = null;
        _branchProcessor = null;
        _rawJsonBytes = null;
    }
}
