// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Visitors;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Central factory for creating DI containers that wire block processing components
/// via production <see cref="BlockProcessingModule"/>, matching the real Nethermind pipeline.
/// Benchmark-specific overrides (genesis state, stub IBlockTree) are layered on top.
/// </summary>
internal static class BenchmarkContainer
{
    /// <summary>
    /// Creates a scope with IWorldState + ITransactionProcessor for tx-level benchmarks (EVMExecute, EVMBuildUp).
    /// Dispose the returned scope in GlobalCleanup.
    /// </summary>
    public static ILifetimeScope CreateTransactionScope(ISpecProvider specProvider, IBlocksConfig blocksConfig = null)
    {
        blocksConfig ??= BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();
        InitConfig initConfig = new();

        ContainerBuilder builder = new();
        builder.AddModule(new BlockProcessingModule(initConfig, blocksConfig));
        RegisterBenchmarkOverrides(builder, specProvider, blocksConfig);

        IContainer container = builder.Build();

        // Create a child scope mirroring what MainProcessingContext does:
        // register IWorldStateScopeProvider + validation modules
        ILifetimeScope scope = container.BeginLifetimeScope(childBuilder =>
        {
            childBuilder
                .AddSingleton<IWorldStateScopeProvider>(PayloadLoader.CreateWorldStateScopeProvider())
                .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>();
        });

        return scope;
    }

    /// <summary>
    /// Creates a scope with full block processing components for block-level benchmarks
    /// (BlockOne, Block, BlockBuilding, NewPayload, NewPayloadMeasured).
    /// Returns the scope, optional pre-warmer, and the root container (dispose in GlobalCleanup).
    /// </summary>
    public static (ILifetimeScope Scope, IBlockCachePreWarmer PreWarmer, IDisposable ContainerLifetime)
        CreateBlockProcessingScope(
            ISpecProvider specProvider,
            IBlocksConfig blocksConfig = null,
            bool isBlockBuilding = false,
            Action<ContainerBuilder> additionalRegistrations = null)
    {
        blocksConfig ??= BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();
        InitConfig initConfig = new();

        ContainerBuilder builder = new();
        builder.AddModule(new BlockProcessingModule(initConfig, blocksConfig));
        builder.AddModule(new PrewarmerModule(blocksConfig));
        RegisterBenchmarkOverrides(builder, specProvider, blocksConfig);

        // If prewarming, register IWorldStateManager for PrewarmerEnvFactory
        if (blocksConfig.PreWarmStateOnBlockProcessing)
        {
            builder.AddSingleton<IWorldStateManager>(ctx =>
            {
                NodeStorageCache nodeStorageCache = ctx.Resolve<NodeStorageCache>();
                return PayloadLoader.CreateWorldStateManager(nodeStorageCache);
            });
        }

        IContainer container = builder.Build();

        // Create a child scope applying validation/production modules + prewarmer module,
        // mirroring what MainProcessingContext does with IBlockValidationModule + IMainProcessingModule
        ILifetimeScope scope = container.BeginLifetimeScope(childBuilder =>
        {
            // Register IWorldStateScopeProvider from benchmark genesis state
            if (blocksConfig.PreWarmStateOnBlockProcessing)
            {
                NodeStorageCache nodeStorageCache = container.Resolve<NodeStorageCache>();
                childBuilder.AddSingleton<IWorldStateScopeProvider>(
                    PayloadLoader.CreateWorldStateScopeProvider(nodeStorageCache));

                // Apply prewarmer decorators (PrewarmerScopeProvider, CachedCodeInfoRepository)
                childBuilder.AddModule(new PrewarmerModule.PrewarmerMainProcessingModule());
            }
            else
            {
                childBuilder.AddSingleton<IWorldStateScopeProvider>(
                    PayloadLoader.CreateWorldStateScopeProvider());
            }

            // Register the appropriate tx executor based on mode
            if (isBlockBuilding)
            {
                childBuilder
                    .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
                    .AddScoped<ITransactionProcessorAdapter, BuildUpTransactionProcessorAdapter>();
            }
            else
            {
                childBuilder
                    .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
                    .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>();
            }

            additionalRegistrations?.Invoke(childBuilder);
        });

        IBlockCachePreWarmer preWarmer = blocksConfig.PreWarmStateOnBlockProcessing
            ? scope.Resolve<IBlockCachePreWarmer>()
            : null;

        return (scope, preWarmer, container);
    }

    private static void RegisterBenchmarkOverrides(
        ContainerBuilder builder,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig)
    {
        builder
            .AddSingleton(specProvider)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(blocksConfig)
            .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)
            // Skip block/header validation — benchmark payloads are known-good and the
            // MinimalBenchmarkBlockTree cannot satisfy parent-header lookups.
            .AddSingleton<IBlockValidator>(Always.Valid)
            // Stub IHeaderFinder — BlockhashCache needs it; benchmarks don't look up historical headers.
            .AddSingleton<IHeaderFinder>(new NullHeaderFinder())
            // Stub IBlockTree — only needed for UnclesValidator resolution + BlockchainProcessor.
            // Benchmarks process blocks with no uncles.
            .AddSingleton<IBlockTree>(new MinimalBenchmarkBlockTree())
            .Bind<IBlockFinder, IBlockTree>()
            ;
    }

    private sealed class NullHeaderFinder : IHeaderFinder
    {
        public BlockHeader Get(Hash256 blockHash, long? blockNumber = null) => null;
    }

    /// <summary>
    /// Minimal IBlockTree stub for DI resolution of validators.
    /// Only satisfies interface — all lookups return null/empty.
    /// </summary>
    private sealed class MinimalBenchmarkBlockTree : IBlockTree
    {
        public Hash256 HeadHash => null;
        public Hash256 GenesisHash => null;
        public Hash256 PendingHash => null;
        public Hash256 FinalizedHash => null;
        public Hash256 SafeHash => null;
        public Block Head => null;
        public ulong NetworkId => 1;
        public ulong ChainId => 1;
        public BlockHeader Genesis => null;
        public BlockHeader BestSuggestedHeader { get; set; }
        public Block BestSuggestedBody => null;
        public BlockHeader BestSuggestedBeaconHeader => null;
        public BlockHeader LowestInsertedHeader { get; set; }
        public BlockHeader LowestInsertedBeaconHeader { get; set; }
        public long BestKnownNumber => 0;
        public long BestKnownBeaconNumber => 0;
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

        public Block FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
        public Block FindBlock(long blockNumber, BlockTreeLookupOptions options) => null;
        public bool HasBlock(long blockNumber, Hash256 blockHash) => false;
        public BlockHeader FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) => null;
        public BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) => null;
        public Hash256 FindBlockHash(long blockNumber) => null;
        public bool IsMainChain(BlockHeader blockHeader) => false;
        public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => false;
        public BlockHeader FindBestSuggestedHeader() => null;
        public long GetLowestBlock() => 0;
        public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) => AddBlockResult.Added;
        public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None) { }
        public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None, BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags bodiesWriteFlags = WriteFlags.None) => AddBlockResult.Added;
        public void UpdateHeadBlock(Hash256 blockHash) { }
        public void NewOldestBlock(long oldestBlock) { }
        public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => AddBlockResult.Added;
        public ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess) => ValueTask.FromResult(AddBlockResult.Added);
        public AddBlockResult SuggestHeader(BlockHeader header) => AddBlockResult.Added;
        public bool IsKnownBlock(long number, Hash256 blockHash) => false;
        public bool IsKnownBeaconBlock(long number, Hash256 blockHash) => false;
        public bool WasProcessed(long number, Hash256 blockHash) => false;
        public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false) { }
        public void MarkChainAsProcessed(IReadOnlyList<Block> blocks) { }
        public Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken) => Task.CompletedTask;
        public (BlockInfo Info, ChainLevelInfo Level) GetInfo(long number, Hash256 blockHash) => (null, null);
        public ChainLevelInfo FindLevel(long number) => null;
        public BlockInfo FindCanonicalBlockInfo(long blockNumber) => null;
        public Hash256 FindHash(long blockNumber) => null;
        public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) => new ArrayPoolList<BlockHeader>(0);
        public void DeleteInvalidBlock(Block invalidBlock) { }
        public void DeleteOldBlock(long blockNumber, Hash256 blockHash) { }
        public void ForkChoiceUpdated(Hash256 finalizedBlockHash, Hash256 safeBlockBlockHash) { }
        public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false) => 0;
        public bool IsBetterThanHead(BlockHeader header) => true;
        public void UpdateBeaconMainChain(BlockInfo[] blockInfos, long clearBeaconMainChainStartPoint) { }
        public void RecalculateTreeLevels() { }
    }
}
