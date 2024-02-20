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

            builder.Register((ctx) => Prune.WhenCacheReaches(ctx.Resolve<IPruningConfig>().CacheMb.MB())) // TODO: memory hint should define this
                .As<IPruningStrategy>();
        }
        else
        {
            builder.RegisterInstance(No.Pruning).As<IPruningStrategy>();
            builder.RegisterInstance(Persist.EveryBlock).As<IPersistenceStrategy>();
        }

        builder.RegisterType<TrieStore>()
            .WithAttributeFiltering()
            .SingleInstance()
            .As<IPruningTrieStore>()
            .As<ITrieStore>();
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
