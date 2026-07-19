// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Steps;
using Nethermind.State.Pbt.Sync;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.State.Pbt;

/// <summary>
/// Wires the PBT backend and substitutes it for the backend picked by
/// <c>WorldStateDbDeciderModule</c>: plugin modules load after the core modules, so these
/// last-wins registrations override every decider-selected service, and neither the Patricia nor
/// the flat state graph is ever constructed.
/// </summary>
public class PbtModule(IPbtConfig config) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            // the pbt components
            .AddColumnDatabase<PbtColumns>(DbNames.Pbt)
            .AddSingleton<IPbtPersistence, PbtRocksDbPersistence>()
            // singleton: a second pool would silently halve every hit rate
            .AddSingleton<IPbtResourcePool, PbtResourcePool>()
            .AddSingleton<PbtSnapshotRepository>()
            .AddSingleton<PbtSnapshotCompactor>()
            .AddSingleton<PbtCompactionSchedule>()
            .AddSingleton<PbtPersistenceCoordinator>()
            .AddSingleton<IPbtDbManager, PbtDbManager>()
            .AddSingleton<PbtStateReader>()
            .AddSingleton<PbtWorldStateManager>()
            .Add<PbtOverridableWorldScope>()

            // overrides of the decider-selected services
            .Bind<IWorldStateManager, PbtWorldStateManager>()
            .AddSingleton<IStateBoundary, PbtStateBoundary>()
            .AddSingleton<IPruningTrieStateAdminRpcModule, PruningDisabledAdminRpcModule>()
            .AddSingleton<IFullStateFinder, PbtFullStateFinder>()
            .AddSingleton<ISnapTrieFactory, PbtUnsupportedSnapTrieFactory>()
            .AddSingleton<ITreeSyncStore, PbtUnsupportedTreeSyncStore>();

        // one-shot rebuild from an existing preimage-flat db: open the flat source directly (its
        // module is never loaded here) and register the import step
        if (config.ImportFromPreimageFlat)
        {
            builder
                .AddColumnDatabase<FlatDbColumns>(DbNames.Flat)
                .AddSingleton<IPersistence, PreimageRocksdbPersistence>()
                .AddSingleton<PbtRebuilder>()
                // the import's stem-order sort: recreated per run, untracked by metrics, and left on
                // disk afterwards because the step exits the process the moment the state is committed
                .AddKeyedSingleton<IDb>(PbtImportScratch.DbName, (ctx) => ctx.Resolve<IDbFactory>()
                    .CreateDb(new DbSettings("PbtImportScratch", ScratchDbPath())
                    {
                        DeleteOnStart = true,
                        SkipMetricsTracking = true,
                    }))
                .AddStep(typeof(ImportPbtFromPreimageFlat));
        }
    }

    /// <summary>
    /// The scratch database's path, taken verbatim from <see cref="IPbtConfig.ImportScratchPath"/> when
    /// set — an absolute path there places the scratch on another disk, which is worth doing since it
    /// holds a record per account, slot and code chunk in the source.
    /// </summary>
    private string ScratchDbPath() =>
        string.IsNullOrWhiteSpace(config.ImportScratchPath) ? PbtImportScratch.DbName : config.ImportScratchPath;

    private sealed class PruningDisabledAdminRpcModule : IPruningTrieStateAdminRpcModule
    {
        public ResultWrapper<PruningStatus> admin_prune() => ResultWrapper<PruningStatus>.Success(PruningStatus.Disabled);
    }
}
