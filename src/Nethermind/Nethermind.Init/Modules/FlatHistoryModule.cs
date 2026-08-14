// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Init.FlatHistory;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Network;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.Synchronization.Peers;

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
            // Single shared owner of "which row format, and how are its rows shaped" for every collaborator that
            // needs it - a writer/reader/pruner (and the changeset server/importer, once they migrate onto this
            // too) each resolving their own would risk disagreeing about the format, and duplicates the
            // availability column's floor state across instances that must otherwise stay in lockstep (a floor
            // lowered by a backfill importer must be observed immediately by every other holder).
            .AddSingleton<HistoryAvailability>(ctx =>
                new HistoryAvailability(ctx.Resolve<IColumnsDb<FlatHistoryColumns>>().GetColumnDb(FlatHistoryColumns.AvailableBlocks)))
            .AddSingleton<HistoryRowFormat>(ctx =>
                HistoryRowFormat.Resolve(ctx.Resolve<HistoryAvailability>(), ctx.Resolve<IFlatDbConfig>().HistoryRetentionBlocks > 0))
            .AddSingleton<HistoryReader>()
            .AddSingleton<HistoryWriter>()
            .AddSingleton<HistoryScopeGate>()
            .AddSingleton<IBackfillInterlock>(NullBackfillInterlock.Instance)
            .AddSingleton<HistoryWindowPruner>()
            .AddSingleton<HistoryServer>()
            .Bind<IHistoryServer, HistoryServer>()
            .Bind<IFlatPersistenceCaptureHook, HistoryWriter>()
            .Bind<IStateHistoryCaptureStatus, HistoryWriter>()
            .Bind<IHistoryPivotSeeder, HistoryWriter>()
            .AddSingleton<BlockTreeCloneHeaderSource>()
            .Bind<ICloneHeaderSource, BlockTreeCloneHeaderSource>()
            .AddSingleton<ArchiveCloneVerifier>()
            .AddDecorator<IFlatDbManager>((ctx, inner) => new HistoricalFlatDbManager(
                inner,
                ctx.Resolve<IPersistenceManager>(),
                ctx.Resolve<HistoryReader>(),
                ctx.Resolve<ITrieNodeCache>(),
                ctx.Resolve<IResourcePool>(),
                ctx.Resolve<IMetricsConfig>().EnableDetailedMetric,
                ctx.Resolve<HistoryScopeGate>()))
            .AddStep(typeof(SeedFlatHistoryGenesis))
            .AddStep(typeof(StartHistoryWindowPruner))
            // devp2p client glue for the two peer-fed feeds: peer selection is shared (NHistPeerSelector), the
            // sinks and coordinators are one per feed since ban/alternate-selection policy and the DI-primitive
            // (byte requiredRowFormatVersion) they need differ between "any served scope" (import) and "full
            // clone with a matching row format" (clone).
            .AddSingleton<NHistRecordContributor>()
            .Bind<INodeRecordContributor, NHistRecordContributor>()
            .AddSingleton<NHistPeerSelector>()
            .AddSingleton<NHistImportPeerSink>()
            .AddSingleton<NHistArchiveClonePeerSink>(ctx =>
                new NHistArchiveClonePeerSink(ctx.Resolve<ISyncPeerPool>(), ctx.Resolve<NHistPeerSelector>(), ctx.Resolve<HistoryRowFormat>().FormatVersion))
            .AddSingleton<WindowBackfillCoordinator>()
            .AddSingleton<ArchiveCloneCoordinator>()
            .AddStep(typeof(StartHistoryWindowBackfill))
            .AddStep(typeof(StartArchiveClone));

        builder.RegisterBuildCallback(ctx =>
        {
            IFlatDbConfig flatDbConfig = ctx.Resolve<IFlatDbConfig>();
            ISyncConfig syncConfig = ctx.Resolve<ISyncConfig>();
            syncConfig.HistoryServingEnabled ??= flatDbConfig.HistoryEnabled && flatDbConfig.HistoryRetentionBlocks > 0;

            if (flatDbConfig.HistoryArchiveCloneEnabled && syncConfig.FastSync
                && (!syncConfig.DownloadBodiesInFastSync || !syncConfig.DownloadReceiptsInFastSync
                    || syncConfig.AncientBodiesBarrier > 0 || syncConfig.AncientReceiptsBarrier > 0))
            {
                syncConfig.DownloadBodiesInFastSync = true;
                syncConfig.DownloadReceiptsInFastSync = true;
                syncConfig.AncientBodiesBarrier = 0;
                syncConfig.AncientReceiptsBarrier = 0;
                ctx.Resolve<ILogManager>().GetClassLogger<FlatHistoryModule>().Info(
                    "Flat.HistoryArchiveCloneEnabled targets a complete archive: enabling historical bodies and receipts download back to genesis (Sync.DownloadBodiesInFastSync=true, Sync.DownloadReceiptsInFastSync=true, ancient barriers 0).");
            }
        });
    }
}
