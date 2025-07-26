// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Init.Modules;

/// <summary>
/// Declares the default database and some classes for utility functions.
/// Additional databases can be added by plugin using the <see cref="ContainerBuilderExtensions.AddDatabase"/> or
/// <see cref="ContainerBuilderExtensions.AddColumnDatabase{T}"/> DSL. These just create a keyed <see cref="IDb"/>,
/// so plugins can override individual database separately. To override all, replace <see cref="IDbFactory"/>.
/// These are all lazy as usual and no database is created until a service that require them is created.
/// </summary>
/// <param name="initConfig"></param>
/// <param name="receiptConfig"></param>
/// <param name="syncConfig"></param>
public class DbModule(
    IInitConfig initConfig,
    IReceiptConfig receiptConfig,
    ISyncConfig syncConfig
) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IRocksDbConfigFactory, RocksDbConfigFactory>()
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .AddSingleton<IDbProvider, DbProvider>()
            .AddScoped<IReadOnlyDbProvider, IDbProvider>((dbProvider) => dbProvider.AsReadOnly(false))

            // Allow requesting keyed specialization instead of `IDb`.
            .AddKeyedAdapter<IKeyValueStore, IDb>((db) => db)
            .AddKeyedAdapter<IDbMeta, IDb>((db) => db)
            .AddKeyedAdapter<ITunableDb, IDb>((db) =>
            {
                if (db is ITunableDb tunableDb) return tunableDb;
                return new NoopTunableDb();
            })
            .AddKeyedAdapter<IReadOnlyKeyValueStore, IKeyValueStore>((kv) => kv)

            // Monitoring use these to track active db. We intercept db factory to keep them lazy. Does not
            // track db that is not created by db factory though...
            .AddSingleton<DbTracker>()
            .AddDecorator<IDbFactory, DbTracker.DbFactoryInterceptor>()

            .AddDatabase(DbNames.State)
            .AddDatabase(DbNames.Code)
            .AddDatabase(DbNames.Metadata)
            .AddDatabase(DbNames.BlockNumbers)
            .AddDatabase(DbNames.BadBlocks)
            .AddDatabase(DbNames.Blocks)
            .AddDatabase(DbNames.Headers)
            .AddDatabase(DbNames.BlockInfos)
            .AddDatabase(DbNames.BadBlocks)
            .AddDatabase(DbNames.Bloom)
            .AddDatabase(DbNames.Metadata)
            .AddDatabase(DbNames.BlobTransactions)

            .AddColumnDatabase<ReceiptsColumns>(DbNames.Receipts)
            .AddColumnDatabase<BlobTxsColumns>(DbNames.BlobTransactions)
            ;

        switch (initConfig.DiagnosticMode)
        {
            case DiagnosticMode.MemDb:
                builder.AddSingleton<IDbFactory, MemDbFactory>();
                break;
            case DiagnosticMode.RpcDb:
                builder.AddDecorator<IDbFactory>(CreateRpcDbFactory);
                break;
            case DiagnosticMode.ReadOnlyDb:
                builder.AddDecorator<IDbFactory, ReadOnlyDbFactory>();
                break;
        }

        // Change receipt db to readonlycolumndb if receipt is disabled
        bool useReceiptsDb = receiptConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync;
        if (!useReceiptsDb)
        {
            builder.AddSingleton<IColumnsDb<ReceiptsColumns>>((_) => new ReadOnlyColumnsDb<ReceiptsColumns>(new MemColumnsDb<ReceiptsColumns>(), false));
        }

    }

    private IDbFactory CreateRpcDbFactory(
        IComponentContext ctx,
        IDbFactory baseDbFactory)
    {
        IJsonSerializer jsonSerializer = ctx.Resolve<IJsonSerializer>();
        ILogManager logManager = ctx.Resolve<ILogManager>();

        RpcDbFactory rpcDbFactory = new(
            baseDbFactory,
            jsonSerializer,
            new BasicJsonRpcClient(
                new Uri(initConfig.RpcDbUrl),
                jsonSerializer,
                logManager
            ), logManager);
        return rpcDbFactory;
    }

    private class ReadOnlyDbFactory(IDbFactory baseDbFactory) : IDbFactory
    {
        public IDb CreateDb(DbSettings dbSettings)
        {
            return baseDbFactory.CreateDb(dbSettings).AsReadOnly(true);
        }

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            return baseDbFactory.CreateColumnsDb<T>(dbSettings).CreateReadOnly(true);
        }
    }
}
