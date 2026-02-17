// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Shared setup helpers for block-level gas benchmarks (BlockOne, Block, NewPayload modes).
/// </summary>
internal static class BlockBenchmarkHelper
{
    public readonly struct BranchProcessingContext(
        IWorldState state,
        IBlockCachePreWarmer preWarmer,
        IDisposable preWarmerLifetime,
        PreBlockCaches preBlockCaches,
        bool cachePrecompiles)
    {
        public IWorldState State { get; } = state;
        public IBlockCachePreWarmer PreWarmer { get; } = preWarmer;
        public IDisposable PreWarmerLifetime { get; } = preWarmerLifetime;
        public PreBlockCaches PreBlockCaches { get; } = preBlockCaches;
        public bool CachePrecompiles { get; } = cachePrecompiles;
    }

    public static BranchProcessingContext CreateBranchProcessingContext(
        ISpecProvider specProvider,
        IBlockhashProvider blockhashProvider)
    {
        IBlocksConfig blocksConfig = new BlocksConfig();
        if (!blocksConfig.PreWarmStateOnBlockProcessing)
        {
            return new BranchProcessingContext(
                PayloadLoader.CreateWorldState(),
                preWarmer: null,
                preWarmerLifetime: null,
                preBlockCaches: null,
                cachePrecompiles: false);
        }

        NodeStorageCache nodeStorageCache = new();
        PreBlockCaches preBlockCaches = new();
        IWorldState state = PayloadLoader.CreateWorldState(
            nodeStorageCache,
            preBlockCaches,
            populatePreBlockCache: false);
        IWorldStateManager worldStateManager = PayloadLoader.CreateWorldStateManager(nodeStorageCache);

        ContainerBuilder containerBuilder = new();
        containerBuilder.RegisterInstance(specProvider).As<ISpecProvider>().SingleInstance();
        containerBuilder.RegisterInstance(blockhashProvider).As<IBlockhashProvider>().SingleInstance();
        containerBuilder.RegisterInstance(LimboLogs.Instance).As<ILogManager>().SingleInstance();
        containerBuilder
            .RegisterInstance(BlobBaseFeeCalculator.Instance)
            .As<ITransactionProcessor.IBlobBaseFeeCalculator>()
            .SingleInstance();
        containerBuilder.RegisterType<WorldState>().As<IWorldState>().InstancePerLifetimeScope();
        containerBuilder.RegisterType<EthereumVirtualMachine>().As<IVirtualMachine>().InstancePerLifetimeScope();
        containerBuilder.RegisterType<CodeInfoRepository>().As<ICodeInfoRepository>().InstancePerLifetimeScope();
        containerBuilder.RegisterType<EthereumPrecompileProvider>().As<IPrecompileProvider>().SingleInstance();
        containerBuilder.RegisterDecorator<ICodeInfoRepository>((context, _, baseCodeInfoRepository) =>
            new CachedCodeInfoRepository(
                context.Resolve<IPrecompileProvider>(),
                baseCodeInfoRepository,
                blocksConfig.CachePrecompilesOnBlockProcessing ? preBlockCaches.PrecompileCache : null));
        containerBuilder.RegisterType<EthereumTransactionProcessor>().As<ITransactionProcessor>().InstancePerLifetimeScope();

        IContainer preWarmerContainer = containerBuilder.Build();
        PrewarmerEnvFactory prewarmerEnvFactory = new(worldStateManager, preWarmerContainer);
        IBlockCachePreWarmer preWarmer = new BlockCachePreWarmer(
            prewarmerEnvFactory,
            blocksConfig,
            nodeStorageCache,
            preBlockCaches,
            LimboLogs.Instance);

        return new BranchProcessingContext(
            state,
            preWarmer,
            preWarmerContainer,
            preBlockCaches,
            blocksConfig.CachePrecompilesOnBlockProcessing);
    }

    public static BlockHeader CreateGenesisHeader() =>
        new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, Array.Empty<byte>())
        {
            StateRoot = PayloadLoader.GenesisStateRoot
        };

    public static ITransactionProcessor CreateTransactionProcessor(
        IWorldState state,
        IBlockhashProvider blockhashProvider,
        ISpecProvider specProvider,
        PreBlockCaches preBlockCaches = null,
        bool cachePrecompiles = false)
    {
        ICodeInfoRepository codeInfoRepo = new EthereumCodeInfoRepository(state);
        if (cachePrecompiles && preBlockCaches is not null)
        {
            codeInfoRepo = new CachedCodeInfoRepository(
                new EthereumPrecompileProvider(),
                codeInfoRepo,
                preBlockCaches.PrecompileCache);
        }

        EthereumVirtualMachine vm = new(blockhashProvider, specProvider, LimboLogs.Instance);

        return new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance, specProvider, state, vm, codeInfoRepo, LimboLogs.Instance);
    }

    public static BlockProcessor CreateBlockProcessor(
        ISpecProvider specProvider, ITransactionProcessor txProcessor, IWorldState state, IReceiptStorage receiptStorage = null) =>
        new(specProvider,
            Always.Valid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor), state),
            state,
            receiptStorage ?? NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, state),
            new BlockhashStore(state),
            LimboLogs.Instance,
            new WithdrawalProcessor(state, LimboLogs.Instance),
            new ExecutionRequestsProcessor(txProcessor));

    public static BlockProcessor CreateBlockBuildingProcessor(
        ISpecProvider specProvider, ITransactionProcessor txProcessor, IWorldState state, IReceiptStorage receiptStorage = null) =>
        new(specProvider,
            Always.Valid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockProductionTransactionsExecutor(
                new BuildUpTransactionProcessorAdapter(txProcessor),
                state,
                new BlockProcessor.BlockProductionTransactionPicker(specProvider),
                LimboLogs.Instance),
            state,
            receiptStorage ?? NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, state),
            new BlockhashStore(state),
            LimboLogs.Instance,
            new WithdrawalProcessor(state, LimboLogs.Instance),
            new ExecutionRequestsProcessor(txProcessor));

    public static void ExecuteSetupPayload(
        IWorldState state, ITransactionProcessor txProcessor,
        BlockHeader preBlockHeader, GasPayloadBenchmarks.TestCase scenario,
        IReleaseSpec spec)
    {
        string setupFile = GasPayloadBenchmarks.FindSetupFile(scenario.FileName);
        if (setupFile is null) return;

        using IDisposable setupScope = state.BeginScope(preBlockHeader);
        (BlockHeader setupHeader, Transaction[] setupTxs) = PayloadLoader.LoadPayload(setupFile);
        txProcessor.SetBlockExecutionContext(setupHeader);
        for (int i = 0; i < setupTxs.Length; i++)
        {
            txProcessor.Execute(setupTxs[i], NullTxTracer.Instance);
        }
        state.Commit(spec);
        state.CommitTree(preBlockHeader.Number);
        preBlockHeader.StateRoot = state.StateRoot;
    }
}
