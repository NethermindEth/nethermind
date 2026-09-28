// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// Experimental plugin that swaps the FlatDb Account/Storage columns for an LMDB-backed store, by decorating
/// <see cref="IDbFactory"/> - the same documented extension point used by <c>RpcDbFactory</c>,
/// <c>ReadOnlyDbFactory</c>, and <c>DbMonitoringModule</c>'s <c>DbFactoryInterceptor</c>. See PLAN-LMDB.md for
/// the full design and rationale.
/// </summary>
/// <remarks>
/// PLAN-LMDB.md calls for a startup guard against this and the sibling BTDB experiment plugin both being enabled
/// at once (they would independently decorate <see cref="IDbFactory"/> and double-wrap the same columns). That
/// guard is deferred: the BTDB plugin/project has deliberately not been imported into this repository (it lives
/// only on the sibling <c>experiment/bitcask</c> branch of a separate clone) - see PLAN-LMDB.md "Location and
/// branch strategy". Add the guard if/when the two ever share a repository.
/// </remarks>
public class LmdbPlugin(ILmdbConfig config) : INethermindPlugin
{
    public string Name => "Lmdb";

    public string Description => "Experimental LMDB (memory-mapped B+tree) backend for the FlatDb Account/Storage columns.";

    public string Author => "Nethermind";

    public bool Enabled => config.Enabled;

    public IModule Module => new LmdbPluginModule();

    private class LmdbPluginModule : Module
    {
        protected override void Load(ContainerBuilder builder) =>
            builder.AddDecorator<IDbFactory, LmdbDbFactoryDecorator>();
    }
}
