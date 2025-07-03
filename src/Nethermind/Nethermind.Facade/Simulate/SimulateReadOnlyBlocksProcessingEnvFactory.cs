// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
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
    ILogManager? logManager = null)
{
    public SimulateReadOnlyBlocksProcessingEnv Create()
    {
        IReadOnlyDbProvider editableDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        IOverridableEnv overridableEnv = overridableEnvFactory.Create();

        BlockTree tempBlockTree = CreateTempBlockTree(editableDbProvider, specProvider, logManager, editableDbProvider);
        BlockTreeOverlay overrideBlockTree = new BlockTreeOverlay(baseBlockTree, tempBlockTree);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(overridableEnv) // worldstate related override here
            .AddSingleton<IBlockTree>(overrideBlockTree)
            .AddSingleton<BlockTreeOverlay>(overrideBlockTree)
            .AddDecorator<IBlockhashProvider, SimulateBlockhashProvider>()
            .AddDecorator<IVirtualMachine, SimulateVirtualMachine>()
            .AddDecorator<IBlockValidator, SimulateBlockValidatorProxy>()
            .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, SimulateBlockValidationTransactionsExecutor>()
            .AddSingleton<ITransactionProcessorAdapter, SimulateTransactionProcessorAdapter>()
            .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)

            .Bind<IBlockProcessor.IBlockTransactionsExecutor, IValidationTransactionExecutor>() // Depend on plugin
            .AddScoped<SimulateRequestState>()
            .AddScoped<SimulateReadOnlyBlocksProcessingEnv>());

        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(envLifetimeScope);
        return envLifetimeScope.Resolve<SimulateReadOnlyBlocksProcessingEnv>();
    }

    private static BlockTree CreateTempBlockTree(IReadOnlyDbProvider readOnlyDbProvider, ISpecProvider? specProvider, ILogManager? logManager, IReadOnlyDbProvider editableDbProvider)
    {
        IBlockStore mainblockStore = new BlockStore(editableDbProvider.BlocksDb);
        IHeaderStore mainHeaderStore = new HeaderStore(editableDbProvider.HeadersDb, editableDbProvider.BlockNumbersDb);
        SimulateDictionaryHeaderStore tmpHeaderStore = new(mainHeaderStore);
        const int badBlocksStored = 1;

        SimulateDictionaryBlockStore tmpBlockStore = new(mainblockStore);
        IBadBlockStore badBlockStore = new BadBlockStore(editableDbProvider.BadBlocksDb, badBlocksStored);

        return new(tmpBlockStore,
            tmpHeaderStore,
            editableDbProvider.BlockInfosDb,
            editableDbProvider.MetadataDb,
            badBlockStore,
            new ChainLevelInfoRepository(readOnlyDbProvider.BlockInfosDb),
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            logManager);
    }
}
