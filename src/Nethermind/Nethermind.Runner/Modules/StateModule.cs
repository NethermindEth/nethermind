// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Witnesses;
using Nethermind.Synchronization.Trie;
using Nethermind.Synchronization.Witness;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Runner.Modules;

public class StateModule: Module
{
    private readonly bool _trieHealingEnabled;
    private readonly bool _witnessProtocolEnabled;
    private readonly PruningMode _pruningMode;
    private readonly FullPruningTrigger _fullPruningTrigger;

    public StateModule(IConfigProvider configProvider)
    {
        ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
        _trieHealingEnabled = syncConfig.TrieHealing;
        _witnessProtocolEnabled = syncConfig.WitnessProtocolEnabled;
        IPruningConfig pruningConfig = configProvider.GetConfig<IPruningConfig>();
        _pruningMode = pruningConfig.Mode;
        _fullPruningTrigger = pruningConfig.FullPruningTrigger;
    }

    // Used for testing
    public StateModule()
    {
        _trieHealingEnabled = true;
        _witnessProtocolEnabled = true;
        _pruningMode = PruningMode.None;
        _fullPruningTrigger = FullPruningTrigger.Manual;
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.Register(ctx =>
            {
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                IDb stateDb = ctx.ResolveKeyed<IDb>(DbNames.State);

                NodeStorageFactory nodeStorageFactory = new NodeStorageFactory(initConfig.StateDbKeyScheme, ctx.Resolve<ILogManager>());;
                nodeStorageFactory.DetectCurrentKeySchemeFrom(stateDb);

                if (nodeStorageFactory.CurrentKeyScheme is INodeStorage.KeyScheme.Hash
                    || initConfig.StateDbKeyScheme == INodeStorage.KeyScheme.Hash)
                {
                    // Special case in case its using hashdb, use a slightly different database configuration.
                    if (stateDb is ITunableDb tunableDb) tunableDb.Tune(ITunableDb.TuneType.HashDb);
                }

                return nodeStorageFactory;
            })
            .As<INodeStorageFactory>()
            .SingleInstance();

        builder.Register(ctx =>
            {
                INodeStorageFactory nodeStorageFactory = ctx.Resolve<INodeStorageFactory>();
                IKeyValueStore state = ctx.ResolveNamed<IKeyValueStore>(DbNames.State);
                return nodeStorageFactory.WrapKeyValueStore(state);
            })
            .As<INodeStorage>()
            .SingleInstance();

        if (_witnessProtocolEnabled)
        {
            builder.RegisterType<WitnessCollector>()
                .WithAttributeFiltering()
                .AsSelf()
                .As<IWitnessCollector>()
                .SingleInstance();
            builder.Register<WitnessCollector, IBlockTree, ILogManager, IWitnessRepository>((wc, btree, log) => wc.WithPruning(btree, log))
                .As<IWitnessRepository>()
                .SingleInstance();

            builder.RegisterDecorator<IKeyValueStoreWithBatching>(
                (ctx, codeDb) => codeDb.WitnessedBy(ctx.Resolve<IWitnessCollector>()),
                DbNames.Code);
            builder.RegisterDecorator<IKeyValueStoreWithBatching>(
                (ctx, stateDb) => stateDb.WitnessedBy(ctx.Resolve<IWitnessCollector>()),
                DbNames.State);
        }
        else
        {
            builder.RegisterInstance(NullWitnessCollector.Instance).As<IWitnessCollector>();
            builder.RegisterInstance(NullWitnessCollector.Instance).As<IWitnessRepository>();
        }

        builder.RegisterDecorator<ISyncConfig>((ctx, _, syncConfig) =>
        {
            if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
            {
                ILogger logger = ctx.Resolve<ILogManager>().GetClassLogger();
                if (logger.IsWarn) logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
                syncConfig.DownloadBodiesInFastSync = true;
            }

            return syncConfig;
        });

        if (_pruningMode.IsMemory())
        {
            builder.Register((ctx) => Persist.IfBlockOlderThan(ctx.Resolve<IPruningConfig>().PersistenceInterval))
                .As<IPersistenceStrategy>();

            if (_pruningMode.IsFull())
            {
                builder.RegisterType<PruningTriggerPersistenceStrategy>().WithAttributeFiltering();
                builder.RegisterDecorator<IPersistenceStrategy>((ctx, _, persistenceStrategy) =>
                {
                    PruningTriggerPersistenceStrategy triggerPersistenceStrategy = ctx.Resolve<PruningTriggerPersistenceStrategy>();
                    return persistenceStrategy.Or(triggerPersistenceStrategy);
                });
            }

            builder.Register((ctx) =>
                {
                    ISyncConfig syncConfig = ctx.Resolve<ISyncConfig>();
                    IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                    IPruningConfig? pruningConfig = ctx.Resolve<IPruningConfig>();
                    INodeStorageFactory nodeStorageFactory = ctx.Resolve<INodeStorageFactory>();
                    ILogger logger = ctx.Resolve<ILogManager>().GetClassLogger(typeof(StateModule));

                    syncConfig.SnapServingEnabled |= syncConfig.SnapServingEnabled is null
                                                     && nodeStorageFactory.CurrentKeyScheme is INodeStorage
                                                         .KeyScheme.HalfPath or null
                                                     && initConfig.StateDbKeyScheme != INodeStorage.KeyScheme.Hash;

                    if (syncConfig.SnapServingEnabled == true && pruningConfig.PruningBoundary < 128)
                    {
                        if (logger.IsWarn)
                            logger.Warn(
                                $"Snap serving enabled, but {nameof(pruningConfig.PruningBoundary)} is less than 128. Setting to 128.");
                        pruningConfig.PruningBoundary = 128;
                    }

                    if (pruningConfig.PruningBoundary < 64)
                    {
                        if (logger.IsWarn) logger.Warn($"Prunig boundary must be at least 64. Setting to 64.");
                        pruningConfig.PruningBoundary = 64;
                    }

                    return Prune
                        .WhenCacheReaches(ctx.Resolve<IPruningConfig>().CacheMb.MB())
                        // Use of ratio, as the effectiveness highly correlate with the amount of keys per snapshot save which
                        // depends on CacheMb. 0.05 is the minimum where it can keep track the whole snapshot.. most of the time.
                        .TrackingPastKeys((int)(pruningConfig.CacheMb.MB() *
                            pruningConfig.TrackedPastKeyCountMemoryRatio / 48))
                        .KeepingLastNState(pruningConfig.PruningBoundary);
                }) // TODO: memory hint should define this
                .As<IPruningStrategy>()
                .SingleInstance();
        }
        else
        {
            builder.RegisterInstance(No.Pruning).As<IPruningStrategy>();
            builder.RegisterInstance(Persist.EveryBlock).As<IPersistenceStrategy>();
        }

        builder.RegisterType<TrieStore>()
            .WithAttributeFiltering()
            .UsingConstructor(
                typeof(INodeStorage),
                typeof(IPruningStrategy),
                typeof(IPersistenceStrategy),
                typeof(ILogManager)
            )
            .SingleInstance()
            .As<ITrieStore>()
            .As<IPruningTrieStore>();

        builder.RegisterType<WorldState>()
            .WithAttributeFiltering()
            .SingleInstance()
            .Keyed<IWorldState>(ComponentKey.MainWorldState);

        if (_trieHealingEnabled)
        {
            builder.RegisterType<HealingTrieStore>()
                .WithAttributeFiltering()
                .SingleInstance()
                .As<ITrieStore>();
            builder.RegisterType<HealingWorldState>()
                .WithAttributeFiltering()
                .SingleInstance()
                .As<IWorldState>();
        }

        builder.RegisterType<WorldStateManager>()
            .WithAttributeFiltering()
            .SingleInstance()
            .As<IWorldStateManager>();

        builder.RegisterType<ChainHeadReadOnlyStateProvider>()
            .As<IReadOnlyStateProvider>();

        builder.Register<IWorldStateManager, IStateReader>(wsm => wsm.GlobalStateReader);

        ConfigureFullPruning(builder);
    }

    private void ConfigureFullPruning(ContainerBuilder builder)
    {
        builder.Register<IFullPruningDb, IInitConfig, string>((db, cfg) => db.GetPath(cfg.BaseDbPath))
            .Keyed<string>(ComponentKey.FullPruningDbPath);
        builder.Register<IPruningConfig, long>(conf => conf.FullPruningThresholdMb.MB())
            .Keyed<long>(ComponentKey.FullPruningThresholdMb);

        switch (_fullPruningTrigger)
        {
            case FullPruningTrigger.StateDbSize:
                builder.RegisterType<PathSizePruningTrigger>().WithAttributeFiltering().As<IPruningTrigger>().SingleInstance();
                break;
            case FullPruningTrigger.VolumeFreeSpace:
                builder.RegisterType<DiskFreeSpacePruningTrigger>().WithAttributeFiltering().As<IPruningTrigger>().SingleInstance();
                break;
        }

        builder.RegisterType<ManualPruningTrigger>().As<IPruningTrigger>().SingleInstance();
        builder.RegisterComposite<CompositePruningTrigger, IPruningTrigger>();

        builder.Register<IComponentContext, IDriveInfo>((fs) =>
            fs.Resolve<IFileSystem>().GetDriveInfos(fs.ResolveKeyed<string>(ComponentKey.FullPruningDbPath)).FirstOrDefault());
        builder.Register<ChainSpec, IChainEstimations>((cs) => ChainSizes.CreateChainSizeInfo(cs.ChainId));
    }
}
