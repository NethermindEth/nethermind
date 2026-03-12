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
using Nethermind.Logging;
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

        IBranchProcessor branchProcessor = _scope.Resolve<IBranchProcessor>();
        RecoverSignatures recoverSignatures = _scope.Resolve<RecoverSignatures>();

        BlockchainProcessor blockchainProcessor = new(
            new BenchmarkBlockProducerBlockTree(_chainParentHeader),
            branchProcessor,
            recoverSignatures,
            BlockBenchmarkHelper.CreateStateReader(state),
            LimboLogs.Instance,
            BlockBenchmarkHelper.GetBlockBuildingBlockchainProcessorOptions(blocksConfig),
            new NoopProcessingStats());
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
        _scope?.Dispose();
        _containerLifetime?.Dispose();
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

    private sealed class NoopProcessingStats : IProcessingStats
    {
        event EventHandler<BlockStatistics> IProcessingStats.NewProcessingStatistics
        {
            add { }
            remove { }
        }

        public void Start() { }

        public void CaptureStartStats() { }

        public void UpdateStats(Block block, BlockHeader baseBlock, long blockProcessingTimeInMicros) { }
    }

    /// <summary>
    /// Block tree stub for block-building benchmarks. Knows about the parent header
    /// so BlockchainProcessor can find the parent block/header during production.
    /// </summary>
    private sealed class BenchmarkBlockProducerBlockTree : BenchmarkBlockTreeBase
    {
        private readonly BlockHeader _parentHeader;
        private readonly Block _head;

        public BenchmarkBlockProducerBlockTree(BlockHeader parentHeader)
        {
            _parentHeader = parentHeader;
            _head = new Block(parentHeader, new BlockBody(Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), null));
            BestSuggestedHeader = parentHeader;
        }

        public override Hash256 HeadHash => _head.Hash;
        public override Hash256 GenesisHash => _parentHeader.Hash;
        public override Block Head => _head;
        public override BlockHeader Genesis => _parentHeader;
        public override Block BestSuggestedBody => _head;
        public override BlockHeader BestSuggestedBeaconHeader => _parentHeader;
        public override long BestKnownNumber => _parentHeader.Number;
        public override long BestKnownBeaconNumber => _parentHeader.Number;
        public override long GetLowestBlock() => _parentHeader.Number;

        public override Block FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
            blockHash == _parentHeader.Hash ? _head : null;
        public override Block FindBlock(long blockNumber, BlockTreeLookupOptions options) =>
            blockNumber == _parentHeader.Number ? _head : null;
        public override bool HasBlock(long blockNumber, Hash256 blockHash) =>
            blockNumber == _parentHeader.Number && blockHash == _parentHeader.Hash;
        public override BlockHeader FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
            blockHash == _parentHeader.Hash ? _parentHeader : null;
        public override BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) =>
            blockNumber == _parentHeader.Number ? _parentHeader : null;
        public override Hash256 FindBlockHash(long blockNumber) =>
            blockNumber == _parentHeader.Number ? _parentHeader.Hash : null;
        public override Hash256 FindHash(long blockNumber) =>
            blockNumber == _parentHeader.Number ? _parentHeader.Hash : null;
        public override bool IsMainChain(BlockHeader blockHeader) => blockHeader?.Hash == _parentHeader.Hash;
        public override bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => blockHash == _parentHeader.Hash;
        public override bool IsKnownBlock(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;
        public override bool IsKnownBeaconBlock(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;
        public override bool WasProcessed(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;
    }

}
