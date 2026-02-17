// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay gas-benchmark payload files via BranchProcessor.Process,
/// matching the real Nethermind block processing pipeline. Includes state scope management,
/// CommitTree, pre-warming coordination, beacon root, blockhash store, transaction execution,
/// bloom filters, receipts root, withdrawals, execution requests, and state root recalculation.
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasBlockBenchmarks
{
    private IWorldState _state;
    private BranchProcessor _branchProcessor;
    private Block[] _blocksToProcess;
    private BlockHeader _preBlockHeader;
    private IBlockCachePreWarmer _preWarmer;
    private IDisposable _preWarmerLifetime;
    private ProcessingOptions _processingOptions;

    [ParamsSource(nameof(GetTestCases))]
    public GasPayloadBenchmarks.TestCase Scenario { get; set; }

    public static IEnumerable<GasPayloadBenchmarks.TestCase> GetTestCases() => GasPayloadBenchmarks.GetTestCases();

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);

        PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, pragueSpec);

        TestBlockhashProvider blockhashProvider = new();
        BlockBenchmarkHelper.BranchProcessingContext branchProcessingContext = BlockBenchmarkHelper.CreateBranchProcessingContext(specProvider, blockhashProvider);
        _state = branchProcessingContext.State;
        _preWarmer = branchProcessingContext.PreWarmer;
        _preWarmerLifetime = branchProcessingContext.PreWarmerLifetime;
        _preBlockHeader = BlockBenchmarkHelper.CreateGenesisHeader();
        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            _state,
            blockhashProvider,
            specProvider,
            branchProcessingContext.PreBlockCaches,
            branchProcessingContext.CachePrecompiles);

        BlockBenchmarkHelper.ExecuteSetupPayload(_state, txProcessor, _preBlockHeader, Scenario, pragueSpec);

        ReceiptConfig receiptConfig = new();
        IReceiptStorage receiptStorage = receiptConfig.StoreReceipts ? new InMemoryReceiptStorage() : NullReceiptStorage.Instance;
        _processingOptions = receiptConfig.StoreReceipts
            ? ProcessingOptions.StoreReceipts
            : ProcessingOptions.None;

        BlockProcessor blockProcessor = BlockBenchmarkHelper.CreateBlockProcessor(
            specProvider, txProcessor, _state, receiptStorage);

        _branchProcessor = new BranchProcessor(
            blockProcessor, specProvider, _state,
            new BeaconBlockRootHandler(txProcessor, _state),
            blockhashProvider, LimboLogs.Instance, _preWarmer);

        _blocksToProcess = [PayloadLoader.LoadBlock(Scenario.FilePath)];

        // Warm up and verify correctness
        Block[] result = _branchProcessor.Process(
            _preBlockHeader, _blocksToProcess,
            _processingOptions,
            NullBlockTracer.Instance);
        PayloadLoader.VerifyProcessedBlock(result[0], Scenario.ToString(), Scenario.FilePath);
    }

    [Benchmark]
    public void ProcessBlock()
    {
        _branchProcessor.Process(
            _preBlockHeader, _blocksToProcess,
            _processingOptions,
            NullBlockTracer.Instance);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _preWarmer?.ClearCaches();
        _preWarmerLifetime?.Dispose();
        _state = null;
        _branchProcessor = null;
        _blocksToProcess = null;
        _preWarmer = null;
        _preWarmerLifetime = null;
        _processingOptions = ProcessingOptions.None;
    }
}
