// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class Driver : IDisposable
{
    private readonly ILogger _logger;
    private readonly IDerivationPipeline _derivationPipeline;
    private readonly IL2BlockTree _l2BlockTree;
    private readonly IEthRpcModule _l2EthRpc;
    private readonly IDerivedBlocksVerifier _derivedBlocksVerifier;
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly IExecutionEngineManager _executionEngineManager;

    public Driver(IL1Bridge l1Bridge,
        IDecodingPipeline decodingPipeline,
        IOptimismEthRpcModule l2EthRpc,
        IL2BlockTree l2BlockTree,
        CLChainSpecEngineParameters engineParameters,
        IExecutionEngineManager executionEngineManager,
        ulong chainId,
        ILogger logger)
    {
        _logger = logger;
        _l2BlockTree = l2BlockTree;
        _l2EthRpc = l2EthRpc;
        _executionEngineManager = executionEngineManager;
        _decodingPipeline = decodingPipeline;
        _derivedBlocksVerifier = new DerivedBlocksVerifier(logger);
        var payloadAttributesDeriver = new PayloadAttributesDeriver(
            chainId,
            new SystemConfigDeriver(engineParameters),
            new DepositTransactionBuilder(chainId, engineParameters),
            logger);
        _derivationPipeline = new DerivationPipeline(payloadAttributesDeriver, _l2BlockTree, l1Bridge, _logger);
    }

    public async Task Run(CancellationToken token)
    {
        await Task.WhenAll(
            _derivationPipeline.Run(token),
            MainLoop(token)
        );
    }

    private async Task MainLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_derivationPipeline.DerivedPayloadAttributes.TryRead(out var derivedPayloadAttributes))
            {
                await OnL2BlocksDerived(derivedPayloadAttributes);
            }
            else if (_decodingPipeline.DecodedBatchesReader.TryPeek(out var decodedBatch))
            {
                ulong parentNumber = decodedBatch.RelTimestamp / 2 - 1;
                // TODO: make it properly
                L2Block? l2Parent = _l2BlockTree.GetBlockByNumber(parentNumber);
                if (l2Parent is not null)
                {
                    await _derivationPipeline.BatchesForProcessing.WriteAsync((l2Parent, decodedBatch), token);
                    await _decodingPipeline.DecodedBatchesReader.ReadAsync(token);
                }
                else
                {
                    if (_l2BlockTree.HeadBlockNumber > parentNumber)
                    {
                        _logger.Error($"Old batch. Skipping");
                        await _decodingPipeline.DecodedBatchesReader.ReadAsync(token);
                    }
                }
            }
        }
    }

    private async Task OnL2BlocksDerived(PayloadAttributesRef payloadAttributes)
    {
        await _executionEngineManager.ProcessNewDerivedPayloadAttributes(payloadAttributes);

        var blockResult = _l2EthRpc.eth_getBlockByNumber(new((long)payloadAttributes.Number), true);

        if (blockResult.Result != Result.Success)
        {
            _logger.Error($"Unable to get block by number: {(long)payloadAttributes.Number}");
            return;
        }

        var block = blockResult.Data;
        var expectedPayloadAttributes = PayloadAttributesFromBlockForRpc(block);

        bool success = _l2BlockTree.TryAddBlock(new L2Block()
        {
            Hash = block.Hash,
            Number = (ulong)block.Number!.Value,
            PayloadAttributes = expectedPayloadAttributes,
            ParentHash = block.ParentHash,
            SystemConfig = payloadAttributes.SystemConfig,
            L1BlockInfo = payloadAttributes.L1BlockInfo,
        });

        if (!success)
        {
            _logger.Error($"Unable to put block into block tree");
            return;
        }
        else
        {
            _logger.Error($"Adding block into l2blockTree: {block.Number}, {block.Hash}");
        }

        _derivedBlocksVerifier.ComparePayloadAttributes(expectedPayloadAttributes,
            payloadAttributes.PayloadAttributes, payloadAttributes.Number);
    }

    private OptimismPayloadAttributes PayloadAttributesFromBlockForRpc(BlockForRpc block)
    {
        ArgumentNullException.ThrowIfNull(block);
        OptimismPayloadAttributes result = new()
        {
            NoTxPool = true,
            EIP1559Params = block.ExtraData.Length == 0 ? null : block.ExtraData[1..],
            GasLimit = block.GasLimit,
            ParentBeaconBlockRoot = block.ParentBeaconBlockRoot,
            PrevRandao = block.MixHash,
            SuggestedFeeRecipient = block.Miner,
            Timestamp = block.Timestamp.ToUInt64(null),
            Withdrawals = block.Withdrawals?.ToArray()
        };
        Transaction[] txs = block.Transactions.Cast<TransactionForRpc>().Select(t => t.ToTransaction()).ToArray();
        result.SetTransactions(txs);

        return result;
    }

    public void Dispose()
    {
    }
}
