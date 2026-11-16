// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.TraceStore.Ots;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

public class TraceStoreModuleTests
{
    [Test]
    public void Registers_db_persisting_block_tracer_for_main_processor_only()
    {
        // The production graph always registers the spec provider, which prices frame transactions' root gas.
        using IContainer container = BuildContainer(new TraceStoreConfig { Enabled = true }, builder => builder
            .AddSingleton<ISpecProvider>(Substitute.For<ISpecProvider>()));

        // The tracer is contributed via an IMainProcessingModule, so it must NOT be resolvable at the root.
        Assert.That(container.Resolve<IEnumerable<IBlockTracer>>(), Is.Empty);

        // Resolving the tracer also resolves its dependencies (the shared serializer and the keyed DB).
        IBlockTracer[] tracers = ResolveMainProcessingTracers(container);
        Assert.That(tracers, Has.One.InstanceOf<DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer>>());
    }

    [Test]
    public void Decorates_trace_module_to_serve_from_db()
    {
        using IContainer container = BuildContainer(new TraceStoreConfig { Enabled = true }, builder => builder
            .AddScoped<ITraceRpcModule>(_ => Substitute.For<ITraceRpcModule>())
            .AddSingleton<IBlockFinder>(Substitute.For<IBlockFinder>())
            // The production graph registers the regenerable key unconditionally (BlockTreeModule), so the test
            // container must too — the plugin resolves the keyed read-only finder.
            .AddKeyedSingleton<IReceiptFinder>(IReceiptFinder.RegenerableKey, Substitute.For<IReceiptFinder>())
            .AddSingleton<IJsonRpcConfig>(new JsonRpcConfig()));

        // TraceModuleFactory resolves ITraceRpcModule inside a nested lifetime scope; the plugin's
        // root-level decorator must reach into that scope and wrap the module with the DB-backed one.
        using ILifetimeScope scope = container.BeginLifetimeScope();
        Assert.That(scope.Resolve<ITraceRpcModule>(), Is.InstanceOf<TraceStoreRpcModule>());
    }

    [Test]
    public void Registers_configured_ots_pool_capacity([Values(null, -1, 0, 1, 4)] int? configuredCapacity)
    {
        TraceStoreConfig config = new() { Enabled = true };
        if (configuredCapacity.HasValue)
            config.OtsModuleConcurrentInstances = configuredCapacity.Value;

        IRpcModuleFactory<IOtsRpcModule> factory = Substitute.For<IRpcModuleFactory<IOtsRpcModule>>();
        factory.Create().Returns(_ => Substitute.For<IOtsRpcModule>());
        using IContainer container = BuildContainer(config, builder => builder
            .AddSingleton<IRpcModuleFactory<IOtsRpcModule>>(factory));

        container.Resolve<IEnumerable<RpcModuleInfo>>().Single().Pool.Preload();

        int expectedCapacity = configuredCapacity.HasValue ? int.Max(1, configuredCapacity.Value) : 2;
        factory.Received(expectedCapacity + 1).Create();
    }

    [Test]
    public void Plugin_database_options_compose_with_global_defaults([Values] bool usePluginOptions)
    {
        TraceStoreConfig pluginConfig = new() { Enabled = true };
        if (usePluginOptions)
        {
            pluginConfig.RocksDbOptions = "max_background_jobs=3;max_open_files=20;";
            pluginConfig.AdditionalRocksDbOptions = "max_background_jobs=5;";
        }
        DbConfig dbConfig = new()
        {
            RocksDbOptions = "max_background_jobs=1;max_open_files=10;",
            AdditionalRocksDbOptions = "max_open_files=30;",
            BlocksDbRocksDbOptions = "max_background_jobs=7;",
            WriteAheadLogSync = true,
            ReadAheadSize = 123,
            VerifyChecksum = false,
            EnableFileWarmer = true,
            CompressibilityHint = 0.7,
            FlushOnExit = FlushOnExitMode.Full,
            EnableDbStatistics = true,
            StatsDumpPeriodSec = 42,
            SkipCheckingSstFileSizesOnDbOpen = true
        };
        using IContainer container = BuildRocksDbContainer(pluginConfig, dbConfig);
        IRocksDbConfigFactory factory = container.Resolve<IRocksDbConfigFactory>();

        IRocksDbConfig traceConfig = factory.GetForDatabase(TraceStorePlugin.DbName, null);
        IRocksDbConfig blocksConfig = factory.GetForDatabase(DbNames.Blocks, null);
        IDictionary<string, string> options = DbOnTheRocks.ExtractOptions(traceConfig.RocksDbOptions + traceConfig.AdditionalRocksDbOptions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traceConfig.RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions + pluginConfig.RocksDbOptions));
            Assert.That(traceConfig.AdditionalRocksDbOptions, Is.EqualTo(dbConfig.AdditionalRocksDbOptions + pluginConfig.AdditionalRocksDbOptions));
            Assert.That(options["max_background_jobs"], Is.EqualTo(usePluginOptions ? "5" : "1"));
            Assert.That(options["max_open_files"], Is.EqualTo("30"));
            Assert.That(options["skip_checking_sst_file_sizes_on_db_open"], Is.EqualTo("true"));
            Assert.That(traceConfig.MaxOpenFiles, Is.EqualTo(819));
            Assert.That(traceConfig.WriteAheadLogSync, Is.True);
            Assert.That(traceConfig.ReadAheadSize, Is.EqualTo(123));
            Assert.That(traceConfig.VerifyChecksum, Is.False);
            Assert.That(traceConfig.EnableFileWarmer, Is.True);
            Assert.That(traceConfig.CompressibilityHint, Is.EqualTo(0.7));
            Assert.That(traceConfig.FlushOnExit, Is.EqualTo(FlushOnExitMode.Full));
            Assert.That(traceConfig.EnableDbStatistics, Is.True);
            Assert.That(traceConfig.StatsDumpPeriodSec, Is.EqualTo(42));
            Assert.That(blocksConfig.RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions + dbConfig.BlocksDbRocksDbOptions));
            Assert.That(blocksConfig.AdditionalRocksDbOptions, Is.EqualTo(dbConfig.AdditionalRocksDbOptions));
            Assert.That(factory.GetForDatabase(null, null).RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions));
        }
    }

    [Test]
    public void Plugin_options_require_trailing_semicolon([Values] bool additional)
    {
        TraceStoreConfig pluginConfig = new() { Enabled = true };
        if (additional)
            pluginConfig.AdditionalRocksDbOptions = "max_background_jobs=2";
        else
            pluginConfig.RocksDbOptions = "max_background_jobs=2";
        using IContainer container = BuildRocksDbContainer(pluginConfig, new DbConfig());
        IRocksDbConfigFactory factory = container.Resolve<IRocksDbConfigFactory>();

        Assert.That(() => factory.GetForDatabase(TraceStorePlugin.DbName, null),
            Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains(additional ? nameof(ITraceStoreConfig.AdditionalRocksDbOptions) : nameof(ITraceStoreConfig.RocksDbOptions)));
        Assert.That(() => factory.GetForDatabase(DbNames.Blocks, null), Throws.Nothing);
    }

    private static IContainer BuildRocksDbContainer(ITraceStoreConfig pluginConfig, IDbConfig dbConfig) => new ContainerBuilder()
        .AddSingleton<ILogManager>(LimboLogs.Instance)
        .AddSingleton(dbConfig)
        .AddSingleton<IPruningConfig>(new PruningConfig())
        .AddSingleton<IHardwareInfo>(new TestHardwareInfo(0, 1024))
        .AddSingleton(pluginConfig)
        .AddModule(new DbModule(new InitConfig { DiagnosticMode = DiagnosticMode.MemDb }, new ReceiptConfig(), new SyncConfig()))
        .AddModule(new TraceStorePlugin(pluginConfig, new JsonRpcConfig()).Module!)
        .Build();

    private static IContainer BuildContainer(ITraceStoreConfig config, System.Action<ContainerBuilder>? configure = null)
    {
        ContainerBuilder builder = new ContainerBuilder()
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IDbFactory>(new MemDbFactory())
            .AddSingleton(config)
            .AddModule(new TraceStorePlugin(config, new JsonRpcConfig()).Module!);
        configure?.Invoke(builder);
        return builder.Build();
    }

    // Mirrors how MainProcessingContext applies the registered IMainProcessingModules and seeds the tracers.
    private static IBlockTracer[] ResolveMainProcessingTracers(IContainer root)
    {
        IMainProcessingModule[] modules = root.Resolve<IMainProcessingModule[]>();
        ILifetimeScope scope = root.BeginLifetimeScope(builder => builder.AddModule(modules));
        return scope.Resolve<IEnumerable<IBlockTracer>>().ToArray();
    }
}
