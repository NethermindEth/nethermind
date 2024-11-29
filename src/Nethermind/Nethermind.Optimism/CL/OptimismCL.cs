// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Client;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL;

public class OptimismCL
{
    private readonly Driver _driver;
    private readonly ILogger _logger;
    private readonly OptimismCLP2P _p2p;
    private readonly EthereumL1Bridge _l1Bridge;
    private readonly IOptimismEngineRpcModule _engineRpcModule;
    private readonly CLChainSpecEngineParameters _chainSpecEngineParameters;

    public OptimismCL(ISpecProvider specProvider, CLChainSpecEngineParameters engineParameters, ICLConfig config, IJsonSerializer jsonSerializer, IEthereumEcdsa ecdsa,
        CancellationToken cancellationToken, ITimestamper timestamper, ILogManager logManager,
        IOptimismEngineRpcModule engineRpcModule)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.SequencerPubkey);

        _engineRpcModule = engineRpcModule;
        _logger = logManager.GetClassLogger();
        _chainSpecEngineParameters = engineParameters;

        _p2p = new OptimismCLP2P(specProvider.ChainId, Convert.FromHexString(_chainSpecEngineParameters.SequencerPubkey), timestamper, logManager, engineRpcModule);
        IEthApi ethApi = new EthereumEthApi(config, jsonSerializer, logManager);
        IBeaconApi beaconApi = new EthereumBeaconApi(new Uri(config.L1BeaconApiEndpoint!), jsonSerializer, ecdsa, _logger,
            cancellationToken);
        _l1Bridge = new EthereumL1Bridge(ethApi, beaconApi, config, cancellationToken, logManager);
        _driver = new Driver(_l1Bridge, config, _logger);
    }

    public void Start()
    {
        _p2p.Start();
        // _l1Bridge.Start();
        // _driver.Start();
    }
}
