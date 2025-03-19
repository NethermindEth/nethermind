// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
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

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public OptimismCL(
        ISpecProvider specProvider,
        CLChainSpecEngineParameters engineParameters,
        ICLConfig config,
        IJsonSerializer jsonSerializer,
        IEthereumEcdsa ecdsa,
        ITimestamper timestamper,
        ILogManager logManager,
        IOptimismEthRpcModule l2EthRpc,
        IOptimismEngineRpcModule engineRpcModule)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.UnsafeBlockSigner);
        ArgumentNullException.ThrowIfNull(engineParameters.Nodes);
        ArgumentNullException.ThrowIfNull(config.L1BeaconApiEndpoint);

        var logger = logManager.GetClassLogger();

        IEthApi ethApi = new EthereumEthApi(config, jsonSerializer, logManager);
        IBeaconApi beaconApi = new EthereumBeaconApi(new Uri(config.L1BeaconApiEndpoint), jsonSerializer, ecdsa, logger, _cancellationTokenSource.Token);

        _decodingPipeline = new DecodingPipeline(logger);
        _l1Bridge = new EthereumL1Bridge(ethApi, beaconApi, config, engineParameters, _decodingPipeline, logManager);

        ISystemConfigDeriver systemConfigDeriver = new SystemConfigDeriver(engineParameters);
        _l2Api = new L2Api(l2EthRpc, engineRpcModule, systemConfigDeriver, logger);
        _executionEngineManager = new ExecutionEngineManager(_l2Api, logger);
        _driver = new Driver(
            _l1Bridge,
            _decodingPipeline,
            engineParameters,
            _executionEngineManager,
            _l2Api,
            specProvider.ChainId,
            logger);
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
        SetupTest();

        try
        {
            _executionEngineManager.Initialize();
            await Task.WhenAll(
                _decodingPipeline.Run(_cancellationTokenSource.Token),
                _l1Bridge.Run(_cancellationTokenSource.Token),
                _driver.Run(_cancellationTokenSource.Token)
            );
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _p2p.Dispose();
        _driver.Dispose();
    }

    private void SetupTest()
    {
        var block = _l2Api.GetBlockByNumber(11400000);
        _l1Bridge.SetCurrentL1Head(block.L1BlockInfo.Number, block.L1BlockInfo.BlockHash);
    }
}
