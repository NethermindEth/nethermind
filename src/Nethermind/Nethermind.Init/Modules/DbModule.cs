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
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Init.Modules;

public class DbModule(
    IInitConfig initConfig,
    IReceiptConfig receiptConfig,
    ISyncConfig syncConfig
) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .AddSingleton<IDbProvider, AutofacDbProvider>()
            .AddScoped<IReadOnlyDbProvider, IDbProvider>((dbProvider) => dbProvider.AsReadOnly(false))

            .AddKeyedAdapter<IKeyValueStore, IDb>((db) => db)
            .AddKeyedAdapter<IDbMeta, IDb>((db) => db)
            .AddKeyedAdapter<ITunableDb, IDb>((db) =>
            {
                if (db is ITunableDb tunableDb) return tunableDb;
                return new NoopTunableDb();
            })
            .AddKeyedAdapter<IReadOnlyKeyValueStore, IKeyValueStore>((kv) => kv)

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

        // Monitoring uses this to provide metric. we dont use `IDbProvider` because
        // we want to keep it lazy.
        builder
            .AddSingleton<DbTracker>()
            .AddDecorator<IDbFactory, DbTracker.DbFactoryInterceptor>()
            ;

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
