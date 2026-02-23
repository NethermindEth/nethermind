// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Visitors;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
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
    private static readonly EthereumEcdsa s_ecdsa = new(1);

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

        BranchProcessor branchProcessor = (BranchProcessor)_scope.Resolve<IBranchProcessor>();

        BlockchainProcessor blockchainProcessor = new(
            new BenchmarkBlockProducerBlockTree(_chainParentHeader),
            branchProcessor,
            new RecoverSignatures(s_ecdsa, specProvider, LimboLogs.Instance),
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

    private sealed class BenchmarkBlockProducerBlockTree : IBlockTree
    {
        private readonly BlockHeader _parentHeader;
        private readonly Block _head;

        public BenchmarkBlockProducerBlockTree(BlockHeader parentHeader)
        {
            _parentHeader = parentHeader;
            _head = new Block(parentHeader, new BlockBody(Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), null));
            SyncPivot = (0, Keccak.Zero);
            BestSuggestedHeader = parentHeader;
        }

        public Hash256 HeadHash => _head.Hash;
        public Hash256 GenesisHash => _parentHeader.Hash;
        public Hash256 PendingHash => null;
        public Hash256 FinalizedHash => null;
        public Hash256 SafeHash => null;
        public Block Head => _head;
        public ulong NetworkId => 1;
        public ulong ChainId => 1;
        public BlockHeader Genesis => _parentHeader;
        public BlockHeader BestSuggestedHeader { get; set; }
        public Block BestSuggestedBody => _head;
        public BlockHeader BestSuggestedBeaconHeader => _parentHeader;
        public BlockHeader LowestInsertedHeader { get; set; }
        public BlockHeader LowestInsertedBeaconHeader { get; set; }
        public long BestKnownNumber => _parentHeader.Number;
        public long BestKnownBeaconNumber => _parentHeader.Number;
        public bool CanAcceptNewBlocks => true;
        public (long BlockNumber, Hash256 BlockHash) SyncPivot { get; set; }
        public bool IsProcessingBlock { get; set; }
        public long? BestPersistedState { get; set; }

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock { add { } remove { } }
        public event EventHandler<BlockEventArgs> NewSuggestedBlock { add { } remove { } }
        public event EventHandler<BlockReplacementEventArgs> BlockAddedToMain { add { } remove { } }
        public event EventHandler<BlockEventArgs> NewHeadBlock { add { } remove { } }
        public event EventHandler<OnUpdateMainChainArgs> OnUpdateMainChain { add { } remove { } }
        public event EventHandler<IBlockTree.ForkChoiceUpdateEventArgs> OnForkChoiceUpdated { add { } remove { } }

        public Block FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
            blockHash == _parentHeader.Hash ? _head : null;

        public Block FindBlock(long blockNumber, BlockTreeLookupOptions options) =>
            blockNumber == _parentHeader.Number ? _head : null;

        public bool HasBlock(long blockNumber, Hash256 blockHash) =>
            blockNumber == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public BlockHeader FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
            blockHash == _parentHeader.Hash ? _parentHeader : null;

        public BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) =>
            blockNumber == _parentHeader.Number ? _parentHeader : null;

        public Hash256 FindBlockHash(long blockNumber) =>
            blockNumber == _parentHeader.Number ? _parentHeader.Hash : null;

        public bool IsMainChain(BlockHeader blockHeader) => blockHeader?.Hash == _parentHeader.Hash;

        public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => blockHash == _parentHeader.Hash;

        public BlockHeader FindBestSuggestedHeader() => BestSuggestedHeader;

        public long GetLowestBlock() => _parentHeader.Number;

        public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) =>
            AddBlockResult.Added;

        public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) { }

        public AddBlockResult Insert(
            Block block,
            BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
            BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None,
            WriteFlags bodiesWriteFlags = WriteFlags.None) =>
            AddBlockResult.Added;

        public void UpdateHeadBlock(Hash256 blockHash) { }

        public void NewOldestBlock(long oldestBlock) { }

        public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
            AddBlockResult.Added;

        public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) =>
            ValueTask.FromResult(AddBlockResult.Added);

        public AddBlockResult SuggestHeader(BlockHeader header) => AddBlockResult.Added;

        public bool IsKnownBlock(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public bool IsKnownBeaconBlock(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public bool WasProcessed(long number, Hash256 blockHash) =>
            number == _parentHeader.Number && blockHash == _parentHeader.Hash;

        public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false) { }

        public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) { }

        public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) => Task.CompletedTask;

        public (BlockInfo Info, ChainLevelInfo Level) GetInfo(long number, Hash256 blockHash) => (null, null);

        public ChainLevelInfo FindLevel(long number) => null;

        public BlockInfo FindCanonicalBlockInfo(long blockNumber) => null;

        public Hash256 FindHash(long blockNumber) => blockNumber == _parentHeader.Number ? _parentHeader.Hash : null;

        public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) =>
            new ArrayPoolList<BlockHeader>(0);

        public void DeleteInvalidBlock(Block invalidBlock) { }

        public void DeleteOldBlock(long blockNumber, Hash256 blockHash) { }

        public void ForkChoiceUpdated(Hash256 finalizedBlockHash, Hash256 safeBlockBlockHash) { }

        public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) => 0;

        public bool IsBetterThanHead(BlockHeader header) => true;

        public void UpdateBeaconMainChain(BlockInfo[] blockInfos, long clearBeaconMainChainStartPoint) { }

        public void RecalculateTreeLevels() { }
    }

}
