// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// Runs the flat-state DB on the native Rust library, as <see cref="IFlatDbConfig.Backend"/> selects. Added after the
/// C# flat-state registrations, so whatever it registers replaces them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FlatDbBackend.RustPersistence"/> replaces <see cref="IPersistence"/> with <see cref="RustPersistence"/>
/// over the same flat columns, under the C# snapshot manager.
/// </para>
/// <para>
/// <see cref="FlatDbBackend.Rust"/> runs the world state on the forked flat package, whose snapshots and bundles are
/// native, over the native snapshot manager on the same store: <see cref="IWorldStateManager"/> becomes the fork's
/// wherever the flat one is chosen, and <see cref="IPersistenceManager"/> the native one's shell. The original
/// <see cref="IFlatDbManager"/> answers state queries through the fork's and refuses bundles and snapshots, so nothing
/// of the original package writes or reads a C# snapshot; the C# snapshot repository and compactor refuse to be built.
/// </para>
/// <para>
/// Disposal releases the manager before the store, and the store before the database. Both publish their metrics on
/// every tick of the <see cref="IMonitoringService"/>, when there is one.
/// </para>
/// </remarks>
public class FlatRustWorldStateModule(IFlatDbConfig flatDbConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (flatDbConfig.Backend is not (FlatDbBackend.RustPersistence or FlatDbBackend.Rust))
        {
            throw new InvalidConfigurationException($"FlatDb.Backend={flatDbConfig.Backend} does not run on the Rust library.", -1);
        }

        if (NativeMethods.UnavailableReason is { } reason)
        {
            throw new InvalidConfigurationException($"FlatDb.Backend={flatDbConfig.Backend} cannot start: {reason}", -1);
        }

        builder
            .AddSingleton<RustPersistence>(static ctx =>
            {
                RustPersistence persistence = new(ctx.Resolve<IColumnsDb<FlatDbColumns>>(), ctx.Resolve<IFlatDbConfig>(), ctx.Resolve<ILogManager>());
                ctx.ResolveOptional<IMonitoringService>()?.AddMetricsUpdateAction(persistence.PublishMetrics);
                return persistence;
            })
            .Bind<IPersistence, RustPersistence>();

        if (flatDbConfig.Backend != FlatDbBackend.Rust) return;

        builder
            .AddSingleton<Fork.NativeFlatManager>(static ctx =>
            {
                Fork.NativeFlatManager manager = new(
                    ctx.Resolve<RustPersistence>(),
                    ctx.Resolve<IFlatDbConfig>(),
                    ctx.Resolve<ICompactionSchedule>() as CompactionSchedule
                        ?? throw new InvalidConfigurationException($"FlatDb.Backend={FlatDbBackend.Rust} needs the {nameof(CompactionSchedule)} of the flat DB.", -1),
                    ctx.Resolve<IBlocksConfig>(),
                    ctx.Resolve<IStateHeaderProvider>(),
                    ctx.Resolve<IStatePersistenceBarrier>(),
                    ctx.Resolve<IProcessExitSource>(),
                    ctx.Resolve<IMetricsConfig>(),
                    ctx.Resolve<ILogManager>());
                ctx.ResolveOptional<IMonitoringService>()?.AddMetricsUpdateAction(manager.PublishMetrics);
                return manager;
            })
            .AddSingleton<IPersistenceManager, RustPersistenceManager>()
            .AddSingleton<Fork.RustFlatDbManager>()
            .Bind<Fork.IFlatDbManager, Fork.RustFlatDbManager>()
            .AddSingleton<Fork.FlatStateReader>()
            .AddSingleton<Fork.FlatTrieVerifier, Fork.IFlatDbManager, IPersistence, ILogManager, IInitConfig>((flatDbManager, persistence, logManager, initConfig) =>
                new Fork.FlatTrieVerifier(flatDbManager, persistence, logManager, Path.Combine(initConfig.BaseDbPath, "verify-trie")))
            .Add<Fork.FlatOverridableWorldScope>()
            .AddSingleton<Fork.FlatWorldStateManager>()
            .AddSingleton<Fork.TrieWarmer>()
            .AddSingleton<ITrieWarmer>(flatDbConfig.TrieWarmerWorkerCount == 0
                ? _ => new NoopTrieWarmer()
                : ctx => ctx.Resolve<Fork.TrieWarmer>())
            // The flat world state manager the world state decider builds is swapped for the fork's; its own is inert.
            .AddDecorator<IWorldStateManager>(static (ctx, worldStateManager) =>
                worldStateManager is FlatWorldStateManager ? ctx.Resolve<Fork.FlatWorldStateManager>() : worldStateManager)
            .AddSingleton<IFlatDbManager, FlatDbManagerGuard>()
            .AddSingleton<ISnapshotRepository>(static _ => throw Unsupported(nameof(ISnapshotRepository)))
            .AddSingleton<ISnapshotCompactor>(static _ => throw Unsupported(nameof(ISnapshotCompactor)));
    }

    private static NotSupportedException Unsupported(string service) =>
        new($"FlatDb.Backend={FlatDbBackend.Rust} has no {service}: the native snapshot manager holds and compacts the snapshots.");
}
