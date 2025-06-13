// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;

namespace Nethermind.Db;

public class DbModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .AddScoped<IReadOnlyDbProvider, IDbProvider>((dbProvider) => dbProvider.AsReadOnly(false));

        // TODO: Have hooks that automatically get these
        string[] dbNames = [
            DbNames.State,
            DbNames.Code,
            DbNames.Metadata,
            DbNames.BlockNumbers,
            DbNames.BadBlocks,
            DbNames.Blocks,
            DbNames.Headers,
            DbNames.BlockInfos,
            DbNames.BadBlocks,
            DbNames.Bloom,
            DbNames.Metadata,
        ];
        foreach (string dbName in dbNames)
        {
            ConfigureDb(builder, dbName);
        }

        ConfigureColumnDb<ReceiptsColumns>(builder, DbNames.Receipts);
    }

    private static void ConfigureDb(ContainerBuilder builder, string dbName)
    {
        builder.Register((ctx) =>
            {
                IDbProvider dbProvider = ctx.Resolve<IDbProvider>();
                IDb db = dbProvider.GetDb<IDb>(dbName);
                return db;
            })
            .ExternallyOwned()
            .Named<IDb>(dbName)
            .Named<IReadOnlyKeyValueStore>(dbName)
            .Named<IKeyValueStore>(dbName)
            .Named<IDbMeta>(dbName);

        builder.Register((ctx) =>
            {
                IDbProvider dbProvider = ctx.Resolve<IDbProvider>();
                IDb db = dbProvider.GetDb<IDb>(dbName);
                return db as ITunableDb ?? new NoopTunableDb();
            })
            .ExternallyOwned()
            .Named<ITunableDb>(dbName);
    }


    private static void ConfigureColumnDb<TColumn>(ContainerBuilder builder, string dbName)
    {
        builder.Register((ctx) =>
            {
                IDbProvider dbProvider = ctx.Resolve<IDbProvider>();
                IColumnsDb<TColumn> db = dbProvider.GetColumnDb<TColumn>(dbName);
                return db;
            })
            .ExternallyOwned()
            .As<IColumnsDb<TColumn>>();

        builder.Register((ctx) =>
            {
                IDbProvider dbProvider = ctx.Resolve<IDbProvider>();
                IColumnsDb<TColumn> db = dbProvider.GetColumnDb<TColumn>(dbName);
                return db as ITunableDb ?? new NoopTunableDb();
            })
            .ExternallyOwned()
            .Named<ITunableDb>(dbName);
    }
}
