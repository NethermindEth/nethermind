// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.Runner.Modules;

public class DbModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .Register(ctx =>
            {
                INodeStorageFactory nodeStorageFactory = ctx.Resolve<INodeStorageFactory>();
                IDb stateDb = ctx.Resolve<IDbProvider>().StateDb;
                return nodeStorageFactory.WrapKeyValueStore(stateDb);
            })
            .As<INodeStorage>();

        // TODO: Have hooks that automatically get these.
        // TODO: Make these lazy
        string[] dbNames = [
            DbNames.State,
            DbNames.Code,
            DbNames.Metadata,
            DbNames.Blocks,
            DbNames.Headers,
            DbNames.BlockInfos,
            DbNames.BadBlocks,
            DbNames.Bloom,
            DbNames.Metadata,
        ];
        foreach (string dbName in dbNames)
        {
            ConfigureDatabase(builder, dbName);
        }

        // Special case for receipt which uses columns
        ConfigureColumnDb<ReceiptsColumns>(builder, DbNames.Receipts);
    }

    private void ConfigureDatabase(ContainerBuilder builder, string dbName)
    {
        builder
            .Register(ctx =>
            {
                IDbProvider? dbProvider = ctx.Resolve<IDbProvider>();
                return dbProvider.GetDb<IDb>(dbName);
            })
            .Named<IDb>(dbName)
            .Named<IKeyValueStore>(dbName)
            .Named<IReadOnlyKeyValueStore>(dbName)
            .Named<IDbMeta>(dbName);

        builder
            .Register(ctx =>
            {
                IDbProvider? dbProvider = ctx.Resolve<IDbProvider>();
                IDb? db = dbProvider.GetDb<IDb>(dbName);
                return db as ITunableDb ?? new NoopTunableDb();
            })
            .Named<ITunableDb>(dbName);
    }

    private void ConfigureColumnDb<TColumnType>(ContainerBuilder builder, string dbName)
    {
        builder
            .Register(ctx =>
            {
                IDbProvider? dbProvider = ctx.Resolve<IDbProvider>();
                return dbProvider.GetColumnDb<TColumnType>(dbName);
            })
            .Named<IColumnsDb<TColumnType>>(dbName);

        builder
            .Register(ctx =>
            {
                IDbProvider? dbProvider = ctx.Resolve<IDbProvider>();
                IColumnsDb<TColumnType>? db = dbProvider.GetColumnDb<TColumnType>(dbName);
                return db as ITunableDb ?? new NoopTunableDb();
            })
            .Named<ITunableDb>(dbName);
    }
}
