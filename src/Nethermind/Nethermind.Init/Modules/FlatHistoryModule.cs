// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Modules;

/// <summary>
/// Layers historical state (archival reads of blocks below the finalization barrier) on top of the flat world
/// state. Loaded only when history capture is enabled.
/// </summary>
public class FlatHistoryModule : Module
{
    protected override void Load(ContainerBuilder builder) =>
        builder
            .AddColumnDatabase<FlatHistoryColumns>(DbNames.FlatHistory)
            .AddSingleton<HistoryReader>()
            .AddSingleton<HistoryWriter>()
            .AddSingleton<IFlatPersistenceCaptureHook>(ctx => ctx.Resolve<HistoryWriter>())
            .AddDecorator<IFlatDbManager>((ctx, inner) => new HistoricalFlatDbManager(
                inner,
                ctx.Resolve<IPersistenceManager>(),
                ctx.Resolve<HistoryReader>(),
                ctx.Resolve<ITrieNodeCache>(),
                ctx.Resolve<IResourcePool>(),
                ctx.Resolve<IMetricsConfig>().EnableDetailedMetric))
            .AddStep(typeof(SeedFlatHistoryGenesis));
}
