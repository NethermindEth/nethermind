// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay gas-benchmark payload files via BlockProcessor.ProcessOne.
/// Includes full block-level overhead: beacon root, blockhash store, transaction execution,
/// bloom filters, receipts root, withdrawals, execution requests, and state root recalculation.
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasBlockBenchmarks
{
    private IWorldState _state;
    private IDisposable _stateScope;
    private BlockProcessor _blockProcessor;
    private Block _testBlock;
    private BlockHeader _preBlockHeader;
    private IReleaseSpec _spec;

    [ParamsSource(nameof(GetTestCases))]
    public GasPayloadBenchmarks.TestCase Scenario { get; set; }

    public static IEnumerable<GasPayloadBenchmarks.TestCase> GetTestCases() => GasPayloadBenchmarks.GetTestCases();

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);
        _spec = pragueSpec;

        // Load genesis state once (shared across all test cases)
        PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, pragueSpec);

        // Create a fresh WorldState and open scope at genesis
        _state = PayloadLoader.CreateWorldState();
        _preBlockHeader = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, Array.Empty<byte>())
        {
            StateRoot = PayloadLoader.GenesisStateRoot
        };
        _stateScope = _state.BeginScope(_preBlockHeader);

        // Set up EVM infrastructure
        TestBlockhashProvider blockhashProvider = new();
        EthereumCodeInfoRepository codeInfoRepo = new(_state);
        EthereumVirtualMachine vm = new(blockhashProvider, specProvider, LimboLogs.Instance);

        ITransactionProcessor txProcessor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            _state,
            vm,
            codeInfoRepo,
            LimboLogs.Instance);

        // Execute setup payload if one exists for this scenario
        string setupFile = GasPayloadBenchmarks.FindSetupFile(Scenario.FileName);
        if (setupFile is not null)
        {
            (BlockHeader setupHeader, Transaction[] setupTxs) = PayloadLoader.LoadPayload(setupFile);
            txProcessor.SetBlockExecutionContext(setupHeader);
            for (int i = 0; i < setupTxs.Length; i++)
            {
                txProcessor.Execute(setupTxs[i], NullTxTracer.Instance);
            }
            _state.Commit(pragueSpec);
        }

        // Build BlockProcessor with real consensus-critical components, null for persistence/validation
        _blockProcessor = new BlockProcessor(
            specProvider,
            Always.Valid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor),
                _state),
            _state,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, _state),
            new BlockhashStore(_state),
            LimboLogs.Instance,
            new WithdrawalProcessor(_state, LimboLogs.Instance),
            new ExecutionRequestsProcessor(txProcessor));

        // Parse the full test block
        _testBlock = PayloadLoader.LoadBlock(Scenario.FilePath);

        // Warm up: run ProcessOne once to prime code caches, then verify correctness
        (Block processedBlock, _) = _blockProcessor.ProcessOne(_testBlock, ProcessingOptions.NoValidation | ProcessingOptions.ForceProcessing, NullBlockTracer.Instance, _spec, CancellationToken.None);

        // Verify the computed state root and block hash match the expected values from the payload
        (Hash256 expectedStateRoot, Hash256 expectedBlockHash) = ParseExpectedHashes(Scenario.FilePath);
        if (expectedStateRoot is not null && processedBlock.Header.StateRoot != expectedStateRoot)
        {
            throw new InvalidOperationException(
                $"State root mismatch for {Scenario}!\n" +
                $"  Expected: {expectedStateRoot}\n" +
                $"  Computed: {processedBlock.Header.StateRoot}\n" +
                "Block processing produced incorrect results.");
        }

        if (expectedBlockHash is not null && processedBlock.Header.Hash != expectedBlockHash)
        {
            throw new InvalidOperationException(
                $"Block hash mismatch for {Scenario}!\n" +
                $"  Expected: {expectedBlockHash}\n" +
                $"  Computed: {processedBlock.Header.Hash}\n" +
                $"  StateRoot match: {processedBlock.Header.StateRoot == expectedStateRoot}\n" +
                "Block processing produced a different block hash — some header field differs.");
        }

        // Reset state for benchmark iterations
        _stateScope?.Dispose();
        _stateScope = _state.BeginScope(_preBlockHeader);
    }

    private static (Hash256 StateRoot, Hash256 BlockHash) ParseExpectedHashes(string filePath)
    {
        string firstLine;
        using (StreamReader reader = new(filePath))
        {
            firstLine = reader.ReadLine();
        }

        using JsonDocument doc = JsonDocument.Parse(firstLine);
        JsonElement payload = doc.RootElement.GetProperty("params")[0];

        Hash256 stateRoot = null;
        if (payload.TryGetProperty("stateRoot", out JsonElement stateRootEl))
        {
            string hex = stateRootEl.GetString();
            if (hex is not null && hex.Length > 2)
                stateRoot = new Hash256(Bytes.FromHexString(hex));
        }

        Hash256 blockHash = null;
        if (payload.TryGetProperty("blockHash", out JsonElement blockHashEl))
        {
            string hex = blockHashEl.GetString();
            if (hex is not null && hex.Length > 2)
                blockHash = new Hash256(Bytes.FromHexString(hex));
        }

        return (stateRoot, blockHash);
    }

    [Benchmark]
    public void ProcessBlock()
    {
        _blockProcessor.ProcessOne(_testBlock, ProcessingOptions.NoValidation | ProcessingOptions.ForceProcessing, NullBlockTracer.Instance, _spec, CancellationToken.None);

        // Revert state for next invocation: dispose scope (calls Reset), re-open at pre-block state
        _stateScope?.Dispose();
        _stateScope = _state.BeginScope(_preBlockHeader);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _stateScope?.Dispose();
        _stateScope = null;
        _state = null;
        _blockProcessor = null;
        _testBlock = null;
    }
}
