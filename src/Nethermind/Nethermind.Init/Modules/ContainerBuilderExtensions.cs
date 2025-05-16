// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;

namespace Nethermind.Init.Modules;

public static class ContainerBuilderExtensions
{
    /// <summary>
    /// Register some set of component that is meant to expose `INetworkStorage`.
    /// These are stored outside of rocksdb using `SimpleFilePublicKeyDb`.
    /// </summary>
    /// <param name="builder">The container builder</param>
    /// <param name="dbName">Service key</param>
    /// <param name="storePath">Path relative to BaseDbPath to store db</param>
    /// <returns></returns>
    public static ContainerBuilder AddNetworkStorage(this ContainerBuilder builder, string dbName, string storePath)
    {
        return builder
            .AddKeyedSingleton<IFullDb>(dbName, ctx =>
            {
                ILogManager logManager = ctx.Resolve<ILogManager>();
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();

                return initConfig.DiagnosticMode == DiagnosticMode.MemDb
                    ? new MemDb(dbName)
                    : new SimpleFilePublicKeyDb(dbName, storePath.GetApplicationResourcePath(initConfig.BaseDbPath),
                        logManager);
            })
            .AddKeyedSingleton<IDb>(dbName, ctx => ctx.ResolveKeyed<IFullDb>(dbName))
            .AddKeyedSingleton<INetworkStorage>(dbName, ctx =>
            {
                ILogManager logManager = ctx.Resolve<ILogManager>();
                IFullDb db = ctx.ResolveKeyed<IFullDb>(dbName);
                return new NetworkStorage(db, logManager);
            });
    }
}
