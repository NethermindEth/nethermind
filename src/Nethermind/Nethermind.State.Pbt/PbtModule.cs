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
            .AddSingleton<PbtSnapshotRepository>()
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
                .AddStep(typeof(ImportPbtFromPreimageFlat));
        }
    }

    private sealed class PruningDisabledAdminRpcModule : IPruningTrieStateAdminRpcModule
    {
        public ResultWrapper<PruningStatus> admin_prune() => ResultWrapper<PruningStatus>.Success(PruningStatus.Disabled);
    }
}
