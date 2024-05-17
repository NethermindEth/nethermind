// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading;
using Autofac;
using Autofac.Core;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Trie;
using Nethermind.TxPool;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.Runner.Modules;

public class DatabaseModule : Module
{
    private bool _storeReceipts;
    private DiagnosticMode _diagnosticMode;

    public DatabaseModule(IConfigProvider configProvider)
    {
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
        _storeReceipts = initConfig.StoreReceipts || syncConfig.DownloadReceiptsInFastSync;
        _diagnosticMode = initConfig.DiagnosticMode;
    }

    public DatabaseModule()
    {
        // Used for testing
        _storeReceipts = true;
        _diagnosticMode = DiagnosticMode.MemDb;
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        switch (_diagnosticMode)
        {
            case DiagnosticMode.RpcDb:
                builder.RegisterType<RocksDbFactory>()
                    .WithParameter(GetParameter.FromType<IInitConfig>(ParameterKey.DbPath, initConfig => Path.Combine(initConfig.BaseDbPath, "debug")))
                    .SingleInstance();
                builder.Register(RpcDbFactoryFactory)
                    .As<IDbFactory>()
                    .SingleInstance();
                break;
            case DiagnosticMode.ReadOnlyDb:
                builder.RegisterType<RocksDbFactory>()
                    .WithParameter(GetParameter.FromType<IInitConfig>(ParameterKey.DbPath, initConfig => Path.Combine(initConfig.BaseDbPath, "debug")))
                    .As<IDbFactory>()
                    .SingleInstance();
                break;
            case DiagnosticMode.MemDb:
                builder.RegisterImpl<MemDbFactory, IDbFactory>();
                break;
            default:
                builder.RegisterType<RocksDbFactory>()
                    .WithParameter(GetParameter.FromType<IInitConfig>(ParameterKey.DbPath, initConfig => initConfig.BaseDbPath))
                    .As<IDbFactory>()
                    .SingleInstance();
                break;
        }

        // TODO: Move these to their respective module. They don't need to be in the same place anymore.
        RegisterDb(builder, DbNames.Blocks);
        RegisterDb(builder, DbNames.Headers);
        RegisterDb(builder, DbNames.BlockNumbers);
        RegisterDb(builder, DbNames.BlockInfos);
        RegisterDb(builder, DbNames.BadBlocks);
        RegisterDb(builder, DbNames.Code);
        RegisterDb(builder, DbNames.Bloom);
        RegisterDb(builder, DbNames.Metadata);

        RegisterColumnsDb<ReceiptsColumns>(builder, DbNames.Receipts, readOnly: !_storeReceipts);

        // Note: this is lazy
        RegisterColumnsDb<BlobTxsColumns>(builder, DbNames.BlobTransactions);

        builder.Register<IDbFactory, IFileSystem, FullPruningDb>(StateDbFactory)
            .Keyed<IDb>(DbNames.State)
            .Keyed<IKeyValueStore>(DbNames.State)
            .Keyed<IKeyValueStoreWithBatching>(DbNames.State)
            .As<IFullPruningDb>()
            .SingleInstance();

        builder.Register<IFileSystem, IDbProvider>(InitDbProvider)
            .SingleInstance();

        // Needed to declare AttributeFiltering
        builder.RegisterType<BlobTxStorage>()
            .WithAttributeFiltering()
            .SingleInstance();

        builder.Register<IComponentContext, ITxPoolConfig, IBlobTxStorage>(BlobTxStorageConfig)
            .SingleInstance();

        builder.RegisterType<DbRegistry>().SingleInstance().AsSelf();
    }

    private static IBlobTxStorage BlobTxStorageConfig(IComponentContext ctx, ITxPoolConfig txPoolConfig)
    {
        if (txPoolConfig.BlobsSupport.IsPersistentStorage())
        {
            return ctx.Resolve<BlobTxStorage>();
        }

        return NullBlobTxStorage.Instance;
    }

    private static FullPruningDb StateDbFactory(IDbFactory dbFactory, IFileSystem fileSystem)
    {
        DbSettings stateDbSettings = BuildDbSettings(DbNames.State);
        return new FullPruningDb(
            stateDbSettings,
            dbFactory is not MemDbFactory
                ? new FullPruningInnerDbFactory(dbFactory, fileSystem, stateDbSettings.DbPath)
                : dbFactory,
            () => Interlocked.Increment(ref Metrics.StateDbInPruningWrites));
    }

    private static void RegisterDb(ContainerBuilder builder, string dbName)
    {
        builder.Register<IDbFactory, IDb>((dbFactory) => dbFactory.CreateDb(BuildDbSettings(dbName)))
            .OnActivated((e) =>
            {
                e.Context.Resolve<DbRegistry>().RegisterDb(dbName, e.Instance);
            })
            .Keyed<IDb>(dbName)
            .Keyed<IKeyValueStoreWithBatching>(dbName)
            .Keyed<IKeyValueStore>(dbName)
            .SingleInstance();
    }

    private static void RegisterColumnsDb<T>(ContainerBuilder builder, string dbName, bool readOnly = false) where T : struct, Enum
    {
        Func<IDbFactory, IColumnsDb<T>> factory;
        if (readOnly)
        {
            factory = (_) => new ReadOnlyColumnsDb<T>(new MemColumnsDb<T>(), false);
        }
        else
        {
            factory = (dbFactory) => dbFactory.CreateColumnsDb<T>(BuildDbSettings(dbName));
        }

        builder.Register(factory)
            .OnActivated((e) =>
            {
                e.Context.Resolve<DbRegistry>().RegisterDb(dbName, e.Instance);
            })
            .Named<IColumnsDb<T>>(dbName)
            .As<IColumnsDb<T>>() // You don't need name for columns as T enum exist... Unless for sme reason you want more than one.
            .SingleInstance();
    }

    private static string GetTitleDbName(string dbName) => char.ToUpper(dbName[0]) + dbName[1..];

    private static DbSettings BuildDbSettings(string dbName, bool deleteOnStart = false)
    {
        return new(GetTitleDbName(dbName), dbName)
        {
            DeleteOnStart = deleteOnStart
        };
    }

    private IDbProvider InitDbProvider(
        IComponentContext ctx,
        IFileSystem fileSystem
    )
    {
        DbProvider dbProvider = ctx.Resolve<DbProvider>();

        if (_diagnosticMode != DiagnosticMode.ReadOnlyDb)
        {
            return dbProvider;
        }

        return new ReadOnlyDbProvider(dbProvider, _storeReceipts); // ToDo storeReceipts as createInMemoryWriteStore - bug?
    }

    private RpcDbFactory RpcDbFactoryFactory(IComponentContext ctx)
    {
        var rocksDbFactory = ctx.Resolve<RocksDbFactory>();
        var initConfig = ctx.Resolve<IInitConfig>();
        return ctx.Resolve<RpcDbFactory>(
            TypedParameter.From<IDbFactory>(rocksDbFactory),
            TypedParameter.From<IJsonRpcClient>(
                ctx.Resolve<BasicJsonRpcClient>(
                    TypedParameter.From(new Uri(initConfig.RpcDbUrl))
                )
            )
        );
    }
}
