// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.CL.P2P;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL;

public class OptimismCL : IDisposable
{
    private readonly DecodingPipeline _decodingPipeline;
    private readonly IL1Bridge _l1Bridge;
    private readonly IExecutionEngineManager _executionEngineManager;
    private readonly Driver _driver;
    private readonly IL2Api _l2Api;
    private readonly OptimismCLP2P _p2p;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public OptimismCL(
        ISpecProvider specProvider,
        CLChainSpecEngineParameters engineParameters,
        ICLConfig config,
        IJsonSerializer jsonSerializer,
        IEthereumEcdsa ecdsa,
        ITimestamper timestamper,
        ulong l2GenesisTimestamp,
        ILogManager logManager,
        IOptimismEthRpcModule l2EthRpc,
        IOptimismEngineRpcModule engineRpcModule)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.UnsafeBlockSigner);
        ArgumentNullException.ThrowIfNull(engineParameters.Nodes);
        ArgumentNullException.ThrowIfNull(engineParameters.SystemConfigProxy);
        ArgumentNullException.ThrowIfNull(config.L1BeaconApiEndpoint);
        ArgumentNullException.ThrowIfNull(engineParameters.L2BlockTime);

        _logger = logManager.GetClassLogger();
        _l2BlockTime = engineParameters.L2BlockTime.Value;
        _l2GenesisTimestamp = l2GenesisTimestamp;

        IEthApi ethApi = new EthereumEthApi(config, jsonSerializer, logManager);
        IBeaconApi beaconApi = new EthereumBeaconApi(new Uri(config.L1BeaconApiEndpoint), jsonSerializer, ecdsa, _logger);

        _decodingPipeline = new DecodingPipeline(_logger);
        _l1Bridge = new EthereumL1Bridge(ethApi, beaconApi, config, engineParameters, _decodingPipeline, _logger);

        ISystemConfigDeriver systemConfigDeriver = new SystemConfigDeriver(engineParameters.SystemConfigProxy);
        _l2Api = new L2Api(l2EthRpc, engineRpcModule, systemConfigDeriver, _logger);
        _executionEngineManager = new ExecutionEngineManager(_l2Api, _logger);
        _driver = new Driver(
            _l1Bridge,
            _decodingPipeline,
            engineParameters,
            _executionEngineManager,
            _l2Api,
            specProvider.ChainId,
            l2GenesisTimestamp,
            _logger);
        _p2p = new OptimismCLP2P(
            specProvider.ChainId,
            engineParameters.Nodes,
            config,
            engineParameters.UnsafeBlockSigner,
            timestamper,
            logManager,
            _executionEngineManager);
    }

    public async Task Start()
    {
        try
        {
            await _executionEngineManager.Initialize();

            L2Block? finalized = await _l2Api.GetFinalizedBlock();
            if (finalized is null || IsFinalizedHeadTooOld(finalized.Number))
            {
                Task p2pTask = _p2p.Run(_cancellationTokenSource.Token);
                await _executionEngineManager.OnELSynced
                    .ContinueWith(async _ =>
                    {
                        if (_logger.IsInfo) _logger.Info("EL sync completed. Starting Derivation Process");
                        await _l1Bridge.Initialize(_cancellationTokenSource.Token);
                        await Task.WhenAll(
                            p2pTask,
                            _decodingPipeline.Run(_cancellationTokenSource.Token),
                            _l1Bridge.Run(_cancellationTokenSource.Token),
                            _driver.Run(_cancellationTokenSource.Token)
                        );
                    });
            }
            else
            {
                _l1Bridge.Reset(finalized.L1BlockInfo);
                Task decodingPipelineTask = _decodingPipeline.Run(_cancellationTokenSource.Token);
                _driver.Reset(finalized.Number);
                Task driverTask = _driver.Run(_cancellationTokenSource.Token);
                await _l1Bridge.ProcessUntilHead(_cancellationTokenSource.Token);
                await Task.WhenAll(
                    _p2p.Run(_cancellationTokenSource.Token),
                    decodingPipelineTask,
                    _l1Bridge.Run(_cancellationTokenSource.Token),
                    driverTask
                );
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Exception in Optimism CL: {e}");
            throw;
        }
    }

    private readonly ulong _l2BlockTime;
    private readonly ulong _l2GenesisTimestamp;

    /// <summary>
    /// Checks if current head is too old to derive due to blob expiry
    /// </summary>
    private bool IsFinalizedHeadTooOld(ulong finalizedHeadNumber)
    {
        const ulong blobExpiryThresholdSeconds = 1124000; // around 13 days
        ulong currentTimeSeconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ulong finalizedBlockTimestamp = finalizedHeadNumber * _l2BlockTime + _l2GenesisTimestamp;
        return finalizedBlockTimestamp + blobExpiryThresholdSeconds < currentTimeSeconds;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _p2p.Dispose();
        _driver.Dispose();
    }
}
