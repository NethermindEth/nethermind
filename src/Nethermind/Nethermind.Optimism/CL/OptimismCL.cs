// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL;

public class OptimismCL : IDisposable
{
    private readonly ILogger _logger;
    private readonly OptimismCLP2P _p2p;
    private readonly IOptimismEngineRpcModule _engineRpcModule;
    private readonly CLChainSpecEngineParameters _chainSpecEngineParameters;

    public OptimismCL(ISpecProvider specProvider, CLChainSpecEngineParameters engineParameters, ICLConfig config, IJsonSerializer jsonSerializer,
        IEthereumEcdsa ecdsa, ITimestamper timestamper, ILogManager logManager, IOptimismEngineRpcModule engineRpcModule)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.SequencerP2PAddress);
        ArgumentNullException.ThrowIfNull(engineParameters.Nodes);

        _engineRpcModule = engineRpcModule;
        _logger = logManager.GetClassLogger();
        _chainSpecEngineParameters = engineParameters;

        _p2p = new OptimismCLP2P(specProvider.ChainId, engineParameters.Nodes, config, _chainSpecEngineParameters.SequencerP2PAddress, timestamper, logManager, engineRpcModule);
    }

    public void Start()
    {
        _p2p.Start();
    }

    public void Dispose()
    {
        _p2p.Dispose();
    }
}
