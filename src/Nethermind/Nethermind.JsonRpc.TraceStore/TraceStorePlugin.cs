// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Db;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceStorePlugin : INethermindPlugin
{
    private const string DbName = "TraceStore";
    private INethermindApi _api = null!;
    private ITraceStoreConfig _config = null!;
    private IJsonRpcConfig _jsonRpcConfig = null!;
    private IDbWithSpan? _db;
    private TraceStorePruner? _pruner;
    private ILogManager _logManager = null!;
    private ILogger _logger = null!;
    private ITraceSerializer<ParityLikeTxTrace>? _traceSerializer;
    public string Name => DbName;
    public string Description => "Allows to serve traces without the block state, by saving historical traces to DB.";
    public string Author => "Nethermind";
    private bool Enabled => _config?.Enabled == true;

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logManager = _api.LogManager;
        _config = _api.Config<ITraceStoreConfig>();
        _jsonRpcConfig = _api.Config<IJsonRpcConfig>();
        _logger = _logManager.GetClassLogger<TraceStorePlugin>();

        if (Enabled)
        {
            // Setup serialization
            _traceSerializer = new ParityLikeTraceSerializer(_logManager, _config.MaxDepth, _config.VerifySerialized);

            // Setup DB
            _db = (IDbWithSpan)_api.RocksDbFactory!.CreateDb(new RocksDbSettings(DbName, DbName.ToLower()));
            _api.DbProvider!.RegisterDb(DbName, _db);

            //Setup pruning if configured
            if (_config.BlocksToKeep != 0)
            {
                _pruner = new TraceStorePruner(_api.BlockTree!, _db, _config.BlocksToKeep, _logManager);
            }
        }

        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (Enabled)
        {
            if (_logger.IsInfo) _logger.Info($"Starting TraceStore with {_config.TraceTypes} traces.");

            // Setup tracing
            ParityLikeBlockTracer parityTracer = new(_config.TraceTypes);
            DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> dbPersistingTracer =
                new(parityTracer, _db!, _traceSerializer!, _logManager);
            _api.BlockchainProcessor!.Tracers.Add(dbPersistingTracer);
        }

        // Potentially we could add protocol for syncing traces.
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (Enabled && _jsonRpcConfig.Enabled)
        {
            IRpcModuleProvider apiRpcModuleProvider = _api.RpcModuleProvider!;
            if (apiRpcModuleProvider.GetPool(ModuleType.Trace) is IRpcModulePool<ITraceRpcModule> traceModulePool)
            {
                TraceStoreModuleFactory traceModuleFactory = new(traceModulePool.Factory, _db!, _api.BlockTree!, _api.ReceiptFinder!, _traceSerializer!, _logManager, _config.DeserializationParallelization);
                apiRpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, _jsonRpcConfig.Timeout);
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Enabled)
        {
            _pruner?.Dispose();
            _db?.Dispose();
        }

        return default;
    }
}
