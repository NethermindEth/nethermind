// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Init.Modules;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceStorePlugin(ITraceStoreConfig traceStoreConfig) : INethermindPlugin
{
    public const string DbName = "TraceStore";

    public string Name => DbName;
    public string Description => "Allows to serve traces without the block state, by saving historical traces to DB.";
    public string Author => "Nethermind";
    public bool Enabled => traceStoreConfig.Enabled;

    public IModule Module => new TracerStorePluginModule(traceStoreConfig);

    private class TracerStorePluginModule(ITraceStoreConfig traceStoreConfig) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder
                .AddDatabase(DbName)
                .AddSingleton<TraceStorePruner>()
                .AddSingleton<ITraceSerializer<ParityLikeTxTrace>, ITraceStoreConfig, ILogManager>((config, logManager) =>
                    new ParityLikeTraceSerializer(logManager, config.MaxDepth, config.VerifySerialized))
                // Serve trace_* from the trace DB by decorating the trace module that the default
                // factory builds via DI: TraceModuleFactory.Create resolves ITraceRpcModule from a
                // nested lifetime scope that inherits this root decorator.
                // Decorating the module — rather than re-registering the pool as the old
                // InitRpcModules did via RegisterBoundedByCpuCount — deliberately keeps the base
                // trace module's concurrency bound (RpcModules.cs); the previous bump to
                // Environment.ProcessorCount was unintended.
                .AddDecorator<ITraceRpcModule>((ctx, inner) =>
                    new TraceStoreRpcModule(
                        inner,
                        ctx.ResolveKeyed<IDb>(DbName),
                        ctx.Resolve<IBlockFinder>(),
                        ctx.Resolve<IReceiptFinder>(),
                        ctx.Resolve<ITraceSerializer<ParityLikeTxTrace>>(),
                        ctx.Resolve<IJsonRpcConfig>(),
                        ctx.Resolve<ILogManager>(),
                        ctx.Resolve<ITraceStoreConfig>().DeserializationParallelization))
                .AddSingleton<IMainProcessingModule, TraceStoreMainProcessingModule>();

            // Instantiate the pruner when the block tree comes up so it trims old traces during processing.
            if (traceStoreConfig.BlocksToKeep != 0)
                builder.ResolveOnServiceActivation<TraceStorePruner, IBlockTree>();
        }
    }

    private class TraceStoreMainProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) => builder
            .AddSingleton<IBlockTracer, TraceStoreBlockTracer>();
    }

    private class TraceStoreBlockTracer(
        [KeyFilter(DbName)] IDb db,
        ITraceStoreConfig traceStoreConfig,
        ITraceSerializer<ParityLikeTxTrace> traceSerializer,
        ILogManager logManager)
        : DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer>(
            new ParityLikeBlockTracer(traceStoreConfig.TraceTypes),
            db,
            traceSerializer,
            logManager);
}
