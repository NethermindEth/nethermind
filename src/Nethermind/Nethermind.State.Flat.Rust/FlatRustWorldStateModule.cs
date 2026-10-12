// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// Runs the flat-state DB on the native Rust library, as <see cref="IFlatDbConfig.Backend"/> selects. Added after the
/// C# flat-state registrations, so whatever it registers replaces them.
/// </summary>
/// <remarks>
/// <see cref="FlatDbBackend.RustPersistence"/> replaces <see cref="IPersistence"/> with <see cref="RustPersistence"/>
/// over the same flat columns, under the C# snapshot manager. <see cref="FlatDbBackend.Rust"/> also replaces
/// <see cref="IFlatDbManager"/> and <see cref="IPersistenceManager"/> with the shells of the native snapshot manager,
/// which runs over the same store. Disposal releases the manager before the store, and the store before the database.
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
            .AddSingleton<RustPersistence, IColumnsDb<FlatDbColumns>, IFlatDbConfig, ILogManager>(
                static (db, config, logManager) => new RustPersistence(db, config, logManager))
            .Bind<IPersistence, RustPersistence>();

        if (flatDbConfig.Backend != FlatDbBackend.Rust) return;

        builder
            .AddSingleton<NativeFlatManager>(static ctx => new NativeFlatManager(
                ctx.Resolve<RustPersistence>(),
                ctx.Resolve<IFlatDbConfig>(),
                ctx.Resolve<ICompactionSchedule>() as CompactionSchedule
                    ?? throw new InvalidConfigurationException($"FlatDb.Backend={FlatDbBackend.Rust} needs the {nameof(CompactionSchedule)} of the flat DB.", -1),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<IStateHeaderProvider>(),
                ctx.Resolve<IStatePersistenceBarrier>(),
                ctx.Resolve<IProcessExitSource>(),
                ctx.Resolve<ILogManager>()))
            .AddSingleton<IPersistenceManager, RustPersistenceManager>()
            .AddSingleton<IFlatDbManager, RustFlatDbManager>();
    }
}
