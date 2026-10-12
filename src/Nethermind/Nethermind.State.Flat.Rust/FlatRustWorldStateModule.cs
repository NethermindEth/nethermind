// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
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
/// over the same flat columns, under the C# snapshot manager. <see cref="FlatDbBackend.Rust"/> is refused, rather than
/// silently running another backend, until the Rust snapshot manager is ported.
/// </remarks>
public class FlatRustWorldStateModule(IFlatDbConfig flatDbConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (flatDbConfig.Backend != FlatDbBackend.RustPersistence)
        {
            throw new InvalidConfigurationException(
                $"FlatDb.Backend={flatDbConfig.Backend} is not implemented yet. Set FlatDb.Backend=RustPersistence or CSharp.", -1);
        }

        if (NativeMethods.UnavailableReason is { } reason)
        {
            throw new InvalidConfigurationException($"FlatDb.Backend={flatDbConfig.Backend} cannot start: {reason}", -1);
        }

        builder.AddSingleton<IPersistence, IColumnsDb<FlatDbColumns>, IFlatDbConfig, ILogManager>(
            static (db, config, logManager) => new RustPersistence(db, config, logManager));
    }
}
