// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Abstractions;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Synchronization.Test.Modules;

internal class PsudoNethermindModule(IConfigProvider configProvider) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new SynchronizerModule(configProvider.GetConfig<ISyncConfig>()))
            .AddModule(new DbModule())
            .AddSource(new ConfigRegistrationSource())

            .AddSingleton(configProvider)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IDbProvider>(TestMemDbProvider.Init())

            // TODO: These two have the same responsibility?
            .AddSingleton<ChainSpec>(new ChainSpec())
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)

            .AddSingleton<IFileStoreFactory>(new InMemoryDictionaryFileStoreFactory())
            .AddSingleton<IFileSystem>(Substitute.For<IFileSystem>())

            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<DisposableStack>()
            .AddSingleton<IEthereumEcdsa, ISpecProvider>((spec) => new EthereumEcdsa(spec.ChainId))
            .AddSingleton<ITimerFactory, TimerFactory>();

        ConfigureWorldStateManager(builder);
        ConfigureStores(builder);
        ConfigureNetwork(builder);
        ConfigureBlockProcessing(builder);
    }

    private void ConfigureWorldStateManager(ContainerBuilder builder)
    {
        builder
            .AddSingleton<PruningTrieStateFactory>()
            .AddSingleton<PruningTrieStateFactoryOutput>()
            .AddSingleton<IWorldStateManager, PruningTrieStateFactoryOutput>((o) => o.WorldStateManager)

            .AddSingleton<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader);
    }

    private class PruningTrieStateFactoryOutput
    {
        public IWorldStateManager WorldStateManager { get; }

        public PruningTrieStateFactoryOutput(PruningTrieStateFactory factory)
        {
            (IWorldStateManager worldStateManager, INodeStorage mainNodeStorage, CompositePruningTrigger _) = factory.Build();
            WorldStateManager = worldStateManager;
        }
    }


    private void ConfigureNetwork(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IPivot, Pivot>()
            .AddSingleton<IFullStateFinder, FullStateFinder>()
            .AddSingleton<INodeStatsManager, NodeStatsManager>()
            .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)
            .AddSingleton<IBetterPeerStrategy, TotalDifficultyBetterPeerStrategy>();
    }

    private void ConfigureStores(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IFileStoreFactory, IInitConfig>(CreateFileStoreFactory)
            .AddSingleton<IBloomStorage, BloomStorage>()
            .AddSingleton<IHeaderStore, HeaderStore>()
            .AddSingleton<IBlockStore, BlockStore>()
            .AddSingleton<IReceiptsRecovery, ReceiptsRecovery>()
            .AddSingleton<IReceiptStorage, PersistentReceiptStorage>()
            .AddSingleton<IBadBlockStore, IDb, IInitConfig>(CreateBadBlockStore)
            .AddSingleton<IChainLevelInfoRepository, ChainLevelInfoRepository>()
            .AddSingleton<IBlockTree, BlockTree>();
    }

    private void ConfigureBlockProcessing(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IBlockValidator, BlockValidator>()
            .AddSingleton<ITxValidator, ISpecProvider>(CreateTxValidator)
            .AddSingleton<IHeaderValidator, HeaderValidator>()
            .AddSingleton<IUnclesValidator, UnclesValidator>()
            .AddSingleton<ISealValidator>(NullSealEngine.Instance)
            ;
    }

    private ITxValidator CreateTxValidator(ISpecProvider specProvider)
    {
        return new TxValidator(specProvider.ChainId);
    }

    private IFileStoreFactory CreateFileStoreFactory(IInitConfig initConfig)
    {
        return initConfig.DiagnosticMode == DiagnosticMode.MemDb
            ? new InMemoryDictionaryFileStoreFactory()
            : new FixedSizeFileStoreFactory(Path.Combine(initConfig.BaseDbPath, DbNames.Bloom), DbNames.Bloom,
                Bloom.ByteLength);
    }

    private IBadBlockStore CreateBadBlockStore([KeyFilter(DbNames.BadBlocks)] IDb badBlockDb, IInitConfig initConfig)
    {
        return new BadBlockStore(badBlockDb, initConfig.BadBlocksStored ?? 100);
    }
}
