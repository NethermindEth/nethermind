// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Init.Modules;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceStorePlugin(ITraceStoreConfig traceStoreConfig) : INethermindPlugin
{
    public const string DbName = "TraceStore";

    private INethermindApi _api = null!;
    private IJsonRpcConfig _jsonRpcConfig = null!;
    private IDb? _db;
    private ILogManager _logManager = null!;
    private ILogger _logger;
    private ITraceSerializer<ParityLikeTxTrace>? _traceSerializer;
    public string Name => DbName;
    public string Description => "Allows to serve traces without the block state, by saving historical traces to DB.";
    public string Author => "Nethermind";
    public bool Enabled => traceStoreConfig.Enabled;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _jsonRpcConfig = _api.Config<IJsonRpcConfig>();
        _logger = _logManager.GetClassLogger<TraceStorePlugin>();

        // Setup serialization
        _traceSerializer = new ParityLikeTraceSerializer(_logManager, traceStoreConfig.MaxDepth, traceStoreConfig.VerifySerialized);

        // Setup DB
        _db = _api.Context.ResolveKeyed<IDb>(DbName);

        //Setup pruning if configured
        if (traceStoreConfig.BlocksToKeep != 0)
        {
            _api.Context.Resolve<TraceStorePruner>();
        }

        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (_logger.IsInfo) _logger.Info($"Starting TraceStore with {traceStoreConfig.TraceTypes} traces.");

        // Setup tracing
        ParityLikeBlockTracer parityTracer = new(traceStoreConfig.TraceTypes);
        DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> dbPersistingTracer =
            new(parityTracer, _db!, _traceSerializer!, _logManager);
        _api.MainProcessingContext!.BlockchainProcessor!.Tracers.Add(dbPersistingTracer);

        // Potentially we could add protocol for syncing traces.
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (_jsonRpcConfig.Enabled)
        {
            IRpcModuleProvider apiRpcModuleProvider = _api.RpcModuleProvider!;
            if (apiRpcModuleProvider.GetPoolForMethod(nameof(ITraceRpcModule.trace_call)) is IRpcModulePool<ITraceRpcModule> traceModulePool)
            {
                TraceStoreModuleFactory traceModuleFactory = new(traceModulePool.Factory, _db!, _api.BlockTree!, _api.ReceiptFinder!, _traceSerializer!, _logManager, traceStoreConfig.DeserializationParallelization);
                apiRpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, _jsonRpcConfig.Timeout);
            }
        }

        return Task.CompletedTask;
    }

    public IModule Module => new TracerStorePluginModule();

    private class TracerStorePluginModule : Module
    {
        protected override void Load(ContainerBuilder builder) => builder
            .AddDatabase(DbName)
            .AddSingleton<TraceStorePruner>();
    }
}
