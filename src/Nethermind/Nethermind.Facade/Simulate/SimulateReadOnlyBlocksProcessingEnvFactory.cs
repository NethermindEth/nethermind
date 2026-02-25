// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;
using Nethermind.State.Repositories;

namespace Nethermind.Facade.Simulate;

public class SimulateReadOnlyBlocksProcessingEnvFactory(
    IOverridableEnvFactory overridableEnvFactory,
    ILifetimeScope rootLifetimeScope,
    IReadOnlyBlockTree baseBlockTree,
    IDbProvider dbProvider,
    ISpecProvider specProvider,
    IReadOnlyList<IBlockValidationModule> validationModules,
    ILogManager? logManager = null) : ISimulateReadOnlyBlocksProcessingEnvFactory
{
    public ISimulateReadOnlyBlocksProcessingEnv Create()
    {
        IReadOnlyDbProvider editableDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        IOverridableEnv overridableEnv = overridableEnvFactory.Create();

        IHeaderStore mainHeaderStore = new HeaderStore(editableDbProvider.HeadersDb, editableDbProvider.BlockNumbersDb);
        SimulateDictionaryHeaderStore tmpHeaderStore = new(mainHeaderStore);

        IBlockAccessListStore mainBalStore = new BlockAccessListStore(editableDbProvider.BlockAccessListDb);
        // need tmp?

        BlockTree tempBlockTree = CreateTempBlockTree(editableDbProvider, specProvider, logManager, editableDbProvider, tmpHeaderStore, mainBalStore);
        BlockTreeOverlay overrideBlockTree = new BlockTreeOverlay(baseBlockTree, tempBlockTree);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(overridableEnv) // worldstate related override here
            .AddSingleton<IReadOnlyDbProvider>(editableDbProvider)
            .AddSingleton<IBlockTree>(overrideBlockTree)
            .AddSingleton<BlockTreeOverlay>(overrideBlockTree)
            .AddSingleton<IHeaderStore>(tmpHeaderStore)
            .AddSingleton<IHeaderFinder>(c => c.Resolve<IHeaderStore>())
            .AddSingleton<IBlockhashCache, BlockhashCache>()
            .AddModule(validationModules)
            .AddDecorator<IBlockhashProvider, SimulateBlockhashProvider>()
            .AddDecorator<IBlockValidator, SimulateBlockValidatorProxy>()
            .AddDecorator<ITransactionProcessor.IBlobBaseFeeCalculator, SimulateBlobBaseFeeCalculatorDecorator>()
            .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, SimulateBlockValidationTransactionsExecutor>()
            .AddSingleton<ITransactionProcessorAdapter, SimulateTransactionProcessorAdapter>()
            .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)

            .AddScoped<SimulateRequestState>()
            .AddScoped<SimulateReadOnlyBlocksProcessingEnv>());

        envLifetimeScope.Disposer.AddInstanceForDisposal(editableDbProvider);
        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(envLifetimeScope);
        return envLifetimeScope.Resolve<SimulateReadOnlyBlocksProcessingEnv>();
    }

    private static BlockTree CreateTempBlockTree(
        IReadOnlyDbProvider readOnlyDbProvider,
        ISpecProvider? specProvider,
        ILogManager logManager,
        IReadOnlyDbProvider editableDbProvider,
        SimulateDictionaryHeaderStore tmpHeaderStore,
        IBlockAccessListStore tmpBalStore)
    {
        IBlockStore mainBlockStore = new BlockStore(editableDbProvider.BlocksDb);
        const int badBlocksStored = 1;

        SimulateDictionaryBlockStore tmpBlockStore = new(mainBlockStore);
        IBadBlockStore badBlockStore = new BadBlockStore(editableDbProvider.BadBlocksDb, badBlocksStored);

        return new(tmpBlockStore,
            tmpHeaderStore,
            editableDbProvider.BlockInfosDb,
            editableDbProvider.MetadataDb,
            badBlockStore,
            tmpBalStore,
            new ChainLevelInfoRepository(readOnlyDbProvider.BlockInfosDb),
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            new BlockTreeLogHider(logManager));
    }

    private class BlockTreeLogHider(ILogManager baseLogManager) : ILogManager
    {
        public ILogger GetClassLogger<T>()
        {
            if (typeof(T) != typeof(BlockTree))
            {
                return baseLogManager.GetClassLogger<T>();
            }

            // If not debug, hide all log
            ILogger baseLogger = baseLogManager.GetClassLogger<T>();
            return !baseLogger.IsDebug ? NullLogger.Instance : baseLogger;
        }

        public ILogger GetClassLogger(string filePath = "")
        {
            return baseLogManager.GetClassLogger(filePath);
        }

        public ILogger GetLogger(string loggerName)
        {
            return baseLogManager.GetLogger(loggerName);
        }
    }
}
