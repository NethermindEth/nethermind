// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core.Exceptions;
using Nethermind.Db;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// Runs the flat-state DB on the native Rust library, as <see cref="IFlatDbConfig.Backend"/> selects. Added after the
/// C# flat-state registrations, so whatever it registers replaces them.
/// </summary>
/// <remarks>Refuses every backend for now, rather than silently running the C# one: the Rust persistence and snapshot manager are still being ported.</remarks>
public class FlatRustWorldStateModule(IFlatDbConfig flatDbConfig) : Module
{
    protected override void Load(ContainerBuilder builder) =>
        throw new InvalidConfigurationException(
            $"FlatDb.Backend={flatDbConfig.Backend} is not implemented yet. Set FlatDb.Backend=CSharp.", -1);
}
