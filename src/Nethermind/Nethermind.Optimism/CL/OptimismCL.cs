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
    private readonly IOptimismEthRpcModule _l2EthRpc;
    private readonly IL2BlockTree _l2BlockTree;
    private readonly DecodingPipeline _decodingPipeline;
    private readonly IL1Bridge _l1Bridge;
    private readonly ISystemConfigDeriver _systemConfigDeriver;
    private readonly IExecutionEngineManager _executionEngineManager;
    private readonly Driver _driver;
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
        ArgumentNullException.ThrowIfNull(engineParameters.SequencerP2PAddress);
        ArgumentNullException.ThrowIfNull(engineParameters.Nodes);
        ArgumentNullException.ThrowIfNull(config.L1BeaconApiEndpoint);

        var logger = logManager.GetClassLogger();

        IEthApi ethApi = new EthereumEthApi(config, jsonSerializer, logManager);
        IBeaconApi beaconApi = new EthereumBeaconApi(new Uri(config.L1BeaconApiEndpoint), jsonSerializer, ecdsa, logger, _cancellationTokenSource.Token);

        _l2EthRpc = l2EthRpc;
        _l2BlockTree = new L2BlockTree();
        _decodingPipeline = new DecodingPipeline(_cancellationTokenSource.Token, logger); // TODO: Are we using the correct cancellation token here?
        _l1Bridge = new EthereumL1Bridge(ethApi, beaconApi, config, engineParameters, _decodingPipeline, _cancellationTokenSource.Token, logManager);
        _systemConfigDeriver = new SystemConfigDeriver(engineParameters);
        _executionEngineManager = new ExecutionEngineManager(engineRpcModule, _l2EthRpc, logger);
        _driver = new Driver(
            _l1Bridge,
            _decodingPipeline,
            _l2EthRpc,
            _l2BlockTree,
            engineParameters,
            _executionEngineManager,
            _cancellationTokenSource.Token, // TODO: Are we using the correct cancellation token here?
            logger);
        _p2p = new OptimismCLP2P(
            specProvider.ChainId,
            engineParameters.Nodes,
            config,
            engineParameters.SequencerP2PAddress,
            timestamper,
            logManager,
            _executionEngineManager,
            _cancellationTokenSource.Token);
    }

    public async Task Start()
    {
        await SetupTest();
        _decodingPipeline.Start();
        _l1Bridge.Start();
        _executionEngineManager.Initialize();
        _driver.Start();
        await _p2p.Start();
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _p2p.Dispose();
        _driver.Dispose();
    }

    private async Task SetupTest()
    {
        var block = _l2EthRpc.eth_getBlockByNumber(new(10567750), true).Data;
        DepositTransactionForRpc tx = (DepositTransactionForRpc)block.Transactions.First();
        SystemConfig config =
            _systemConfigDeriver.SystemConfigFromL2BlockInfo(tx.Input!, block.ExtraData, (ulong)block.GasLimit);
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(tx.Input!, block.ExtraData);
        L1Block l1Block = await _l1Bridge.GetBlock(l1BlockInfo.Number);
        if (l1Block.Hash != l1BlockInfo.BlockHash)
        {
            throw new ArgumentException("Unexpected block hash");
        }
        _l1Bridge.SetCurrentL1Head(l1BlockInfo.Number, l1BlockInfo.BlockHash);
        L2Block nativeBlock = new()
        {
            Hash = block.Hash,
            Number = (ulong)block.Number!.Value,
            PayloadAttributes = new()
            {
                EIP1559Params = config.EIP1559Params,
                GasLimit = block.GasLimit,
                NoTxPool = true,
                ParentBeaconBlockRoot = block.ParentBeaconBlockRoot,
                PrevRandao = block.MixHash,
                SuggestedFeeRecipient = block.Miner,
                Timestamp = block.Timestamp.ToUInt64(null),
                Transactions = null,
                Withdrawals = Array.Empty<Withdrawal>(),
            },
            ParentHash = block.ParentHash,
            SystemConfig = config,
            L1BlockInfo = l1BlockInfo,
        };
        _l2BlockTree.TryAddBlock(nativeBlock);
    }
}
