// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.State.SnapServer;

namespace Nethermind.Init.Modules;

/// <summary>
/// Layers historical state (archival reads of blocks below the finalization barrier) on top of the flat world
/// state. Loaded only when history capture is enabled.
/// </summary>
public class FlatHistoryModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddColumnDatabase<FlatHistoryColumns>(DbNames.FlatHistory)
            .AddSingleton<HistoryReader>()
            .AddSingleton<HistoryWriter>()
            .AddSingleton<HistoryScopeGate>()
            .AddSingleton<IBackfillInterlock>(NullBackfillInterlock.Instance)
            .AddSingleton<HistoryWindowPruner>()
            .AddSingleton<HistoryServer>()
            .Bind<IHistoryServer, HistoryServer>()
            .Bind<IFlatPersistenceCaptureHook, HistoryWriter>()
            .Bind<IStateHistoryCaptureStatus, HistoryWriter>()
            .AddDecorator<IFlatDbManager>((ctx, inner) => new HistoricalFlatDbManager(
                inner,
                ctx.Resolve<IPersistenceManager>(),
                ctx.Resolve<HistoryReader>(),
                ctx.Resolve<ITrieNodeCache>(),
                ctx.Resolve<IResourcePool>(),
                ctx.Resolve<IMetricsConfig>().EnableDetailedMetric,
                ctx.Resolve<HistoryScopeGate>()))
            .AddStep(typeof(SeedFlatHistoryGenesis))
            .AddStep(typeof(StartHistoryWindowPruner));

        builder.RegisterBuildCallback(ctx =>
        {
            IFlatDbConfig flatDbConfig = ctx.Resolve<IFlatDbConfig>();
            ISyncConfig syncConfig = ctx.Resolve<ISyncConfig>();
            syncConfig.HistoryServingEnabled ??= flatDbConfig.HistoryEnabled && flatDbConfig.HistoryRetentionBlocks > 0;
        });
    }
}
