// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL;

public class OptimismCL : IDisposable
{
    private readonly ILogger _logger;
    private readonly OptimismCLP2P _p2p;
    private readonly IOptimismEngineRpcModule _engineRpcModule;
    private readonly IOptimismEthRpcModule _l2EthRpc;
    private readonly CLChainSpecEngineParameters _chainSpecEngineParameters;
    private readonly IL1Bridge _l1Bridge;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Driver _driver;

    private readonly IBeaconApi _beaconApi;
    private readonly IEthApi _ethApi;

    public OptimismCL(ISpecProvider specProvider, CLChainSpecEngineParameters engineParameters, ICLConfig config,
        IJsonSerializer jsonSerializer, IEthereumEcdsa ecdsa, ITimestamper timestamper, ILogManager logManager,
        IOptimismEthRpcModule l2EthRpc, IOptimismEngineRpcModule engineRpcModule)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.SequencerP2PAddress);
        ArgumentNullException.ThrowIfNull(engineParameters.Nodes);
        ArgumentNullException.ThrowIfNull(config.L1BeaconApiEndpoint);

        _l2EthRpc = l2EthRpc;
        _engineRpcModule = engineRpcModule;
        _logger = logManager.GetClassLogger();
        _chainSpecEngineParameters = engineParameters;

        _p2p = new OptimismCLP2P(specProvider.ChainId, engineParameters.Nodes, config,
            _chainSpecEngineParameters.SequencerP2PAddress, timestamper, logManager, engineRpcModule, _cancellationTokenSource.Token);
        _ethApi = new EthereumEthApi(config, jsonSerializer, logManager);
        _beaconApi = new EthereumBeaconApi(new Uri(config.L1BeaconApiEndpoint), jsonSerializer, ecdsa,
            _logger, _cancellationTokenSource.Token);
        _l1Bridge = new EthereumL1Bridge(_ethApi, _beaconApi, config, _cancellationTokenSource.Token, logManager);
        _driver = new Driver(_l1Bridge, config, engineParameters, _logger);
    }

    public void Start()
    {
        // _l1Bridge.Start();
        _driver.Start();
        TEST();
        // _p2p.Start();
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _p2p.Dispose();
        _driver.Dispose();
    }

    private void TEST()
    {
        BlockForRpc block = _l2EthRpc.eth_getBlockByNumber(BlockParameter.Latest, true).Data;
        var enumerator = block.Transactions.GetEnumerator();
        enumerator.MoveNext();
        OptimismTransactionForRpc tx = (OptimismTransactionForRpc)enumerator.Current;
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(tx.Input, block.ExtraData);
        _logger.Error($"DONE: {l1BlockInfo.Number}, {l1BlockInfo.BlockHash}, {l1BlockInfo.BatcherAddress}");
        GetBatches();
    }

    private async void GetBatches()
    {
        // BlockForRpc? block = await _l1Bridge.GetBlock(21691682);
        BeaconBlock? beaconBlock = await _beaconApi.GetBySlotNumber(10905449);
        ReceiptForRpc[]? receitps = await _ethApi.GetReceiptsByHash(beaconBlock!.Value.ExecutionBlockHash);
        _driver.OnNewL1Head(beaconBlock!.Value, receitps!);
    }
}
