// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks block-building flow using BlockToProduce + BlockProductionTransactionsExecutor
/// with ProcessingOptions.ProducingBlock, matching Nethermind's producer-side transaction path.
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasBlockBuildingBenchmarks
{
    internal const string BuildBlocksOnMainStateEnvVar = "NETHERMIND_BLOCKBUILDING_MAIN_STATE";

    private ILifetimeScope _scope;
    private ILifetimeScope _blockchainScope;
    private IDisposable _containerLifetime;
    private IBlockchainProcessor _chainProcessor;
    private BlockHeader _chainParentHeader;
    private BlockHeader _testHeaderTemplate;
    private Transaction[] _testTransactions;
    private BlockHeader[] _testUncles;
    private Withdrawal[] _testWithdrawals;
    private ProcessingOptions _processingOptions;
    private IBlockCachePreWarmer _preWarmer;

    [ParamsSource(nameof(GetTestCases))]
    public GasPayloadBenchmarks.TestCase Scenario { get; set; }

    public static IEnumerable<GasPayloadBenchmarks.TestCase> GetTestCases() => GasPayloadBenchmarks.GetTestCases();

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);
        BlocksConfig blocksConfig = BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();

        string buildOnMainStateValue = Environment.GetEnvironmentVariable(BuildBlocksOnMainStateEnvVar);
        if (!string.IsNullOrWhiteSpace(buildOnMainStateValue) && bool.TryParse(buildOnMainStateValue, out bool buildOnMainState))
        {
            blocksConfig.BuildBlocksOnMainState = buildOnMainState;
        }

        (_scope, _preWarmer, _containerLifetime) = BenchmarkContainer.CreateBlockProcessingScope(
            specProvider, GasPayloadBenchmarks.s_genesisPath, pragueSpec, blocksConfig, isBlockBuilding: true);
        IWorldState state = _scope.Resolve<IWorldState>();
        BlockHeader preBlockHeader = BlockBenchmarkHelper.CreateGenesisHeader();

        _processingOptions = BlockBenchmarkHelper.GetBlockBuildingProcessingOptions(blocksConfig);

        ITransactionProcessor txProcessor = _scope.Resolve<ITransactionProcessor>();

        Block testBlock = PayloadLoader.LoadBlock(Scenario.FilePath);
        _testHeaderTemplate = testBlock.Header;
        _testTransactions = testBlock.Transactions;
        _testUncles = testBlock.Uncles;
        _testWithdrawals = testBlock.Withdrawals;

        _chainParentHeader = preBlockHeader.Clone();
        _chainParentHeader.Hash = _testHeaderTemplate.ParentHash;
        _chainParentHeader.Number = _testHeaderTemplate.Number > 0 ? _testHeaderTemplate.Number - 1 : 0;
        _chainParentHeader.TotalDifficulty = _testHeaderTemplate.TotalDifficulty is null
            ? UInt256.Zero
            : _testHeaderTemplate.TotalDifficulty - _testHeaderTemplate.Difficulty;

        BlockBenchmarkHelper.ExecuteSetupPayload(state, txProcessor, preBlockHeader, Scenario, specProvider);
        _chainParentHeader.StateRoot = preBlockHeader.StateRoot;

        // Create a child scope with parent-header-dependent overrides and resolve
        // BlockchainProcessor from DI. Autofac auto-wires its constructor parameters
        // (IBlockTree, IBranchProcessor, IBlockPreprocessorStep, IStateReader, ILogManager,
        // BlockchainProcessor.Options, IProcessingStats) from registered services.
        _blockchainScope = _scope.BeginLifetimeScope(childBuilder =>
        {
            childBuilder
                .AddSingleton<IBlockTree>(new BenchmarkSingleParentBlockTree(_chainParentHeader))
                .AddSingleton(BlockBenchmarkHelper.GetBlockBuildingBlockchainProcessorOptions(blocksConfig));
        });
        BlockchainProcessor blockchainProcessor = _blockchainScope.Resolve<BlockchainProcessor>();
        _chainProcessor = new OneTimeChainProcessor(state, blockchainProcessor);

        // Warm up and verify correctness.
        BlockToProduce warmupBlock = CreateBlockToProduce();
        Block warmupResult = _chainProcessor.Process(warmupBlock, _processingOptions, NullBlockTracer.Instance);
        if (warmupResult is null)
        {
            throw new InvalidOperationException("Block building warmup did not produce a processed block.");
        }

        PayloadLoader.VerifyProcessedBlock(warmupResult, Scenario.ToString(), Scenario.FilePath);
    }

    [Benchmark]
    public void ProcessBlock()
    {
        BlockToProduce blockToProduce = CreateBlockToProduce();
        _chainProcessor.Process(blockToProduce, _processingOptions, NullBlockTracer.Instance);
    }

    private BlockToProduce CreateBlockToProduce()
    {
        BlockHeader header = _testHeaderTemplate.Clone();
        if (header.TotalDifficulty is null)
        {
            UInt256 parentTotalDifficulty = _chainParentHeader.TotalDifficulty ?? UInt256.Zero;
            header.TotalDifficulty = parentTotalDifficulty + header.Difficulty;
        }

        return new BlockToProduce(header, _testTransactions, _testUncles, _testWithdrawals);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // Post-benchmark correctness verification: process one more block and verify result.
        if (_chainProcessor is not null && _testHeaderTemplate is not null)
        {
            BlockToProduce verificationBlock = CreateBlockToProduce();
            Block verificationResult = _chainProcessor.Process(verificationBlock, _processingOptions, NullBlockTracer.Instance);
            if (verificationResult is null)
            {
                throw new InvalidOperationException(
                    $"Post-benchmark verification failed for {Scenario}: block processing returned null.");
            }

            PayloadLoader.VerifyProcessedBlock(verificationResult, Scenario.ToString(), Scenario.FilePath);
        }

        _chainProcessor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _preWarmer?.ClearCaches();
        _blockchainScope?.Dispose();
        _scope?.Dispose();
        _containerLifetime?.Dispose();
        _blockchainScope = null;
        _scope = null;
        _containerLifetime = null;
        _chainProcessor = null;
        _chainParentHeader = null;
        _preWarmer = null;
        _testHeaderTemplate = null;
        _testTransactions = null;
        _testUncles = null;
        _testWithdrawals = null;
    }
}
