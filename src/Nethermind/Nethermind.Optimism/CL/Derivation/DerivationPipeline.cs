// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL.Derivation;

public class DerivationPipeline : IDerivationPipeline
{
    private readonly IPayloadAttributesDeriver _payloadAttributesDeriver;
    private readonly IL2BlockTree _l2BlockTree;
    private readonly IL1Bridge _l1Bridge;
    private readonly ILogger _logger;

    public DerivationPipeline(IPayloadAttributesDeriver payloadAttributesDeriver, IL2BlockTree l2BlockTree,
        IL1Bridge l1Bridge, ILogger logger)
    {
        _payloadAttributesDeriver = payloadAttributesDeriver;
        _l2BlockTree = l2BlockTree;
        _l1Bridge = l1Bridge;
        _logger = logger;
    }

    public async Task<(OptimismPayloadAttributes[], SystemConfig[], L1BlockInfo[])> ConsumeV1Batches(L2Block l2Parent, BatchV1[] batches)
    {
        List<OptimismPayloadAttributes> payloadAttributes = new();
        List<SystemConfig> systemConfigs = new();
        List<L1BlockInfo> l1BlockInfos = new();
        foreach (BatchV1 batch in batches)
        {
            var oneBatchProcessingResult = await ProcessOneBatch(l2Parent, batch);
            if (oneBatchProcessingResult is not null)
            {
                payloadAttributes.AddRange(oneBatchProcessingResult.Value.Item1);
                systemConfigs.AddRange(oneBatchProcessingResult.Value.Item2);
                l1BlockInfos.AddRange(oneBatchProcessingResult.Value.Item3);
            }
        }
        return (payloadAttributes.ToArray(), systemConfigs.ToArray(), l1BlockInfos.ToArray());
    }

    private async Task<(OptimismPayloadAttributes[], SystemConfig[], L1BlockInfo[])?> ProcessOneBatch(L2Block l2Parent, BatchV1 batch)
    {
        _logger.Error($"Processing batch RelTimestamp: {batch.RelTimestamp}");
        ulong expectedParentNumber = batch.RelTimestamp / 2 - 1;
        ArgumentNullException.ThrowIfNull(l2Parent);
        if (expectedParentNumber < l2Parent.Number)
        {
            _logger.Error($"Old batch. Skipping. Expected {expectedParentNumber}, got {l2Parent.Number}");
            return null;
        }

        if (!l2Parent.Hash.Bytes.StartsWith(batch.ParentCheck))
        {
            _logger.Error($"Unexpected L2Parent. Got: {l2Parent.Hash}, ParentCheck: {batch.ParentCheck.ToHexString()}");
            throw new ArgumentException("Wrong L2 parent");
        }

        // TODO: proper error handling
        ulong numberOfL1Origins = GetNumberOfBits(batch.OriginBits) + 1;
        ulong lastL1OriginNum = batch.L1OriginNum;
        BlockForRpc? lastL1Origin = await _l1Bridge.GetBlock(lastL1OriginNum);
        ArgumentNullException.ThrowIfNull(lastL1Origin);
        if (!lastL1Origin.Hash.Bytes.StartsWith(batch.L1OriginCheck))
        {
            // TODO: potential L1 reorg
            throw new ArgumentException("Invalid batch origin");
        }
        BlockForRpc[] l1Origins = new BlockForRpc[numberOfL1Origins];
        ReceiptForRpc[][] l1Receipts = new ReceiptForRpc[numberOfL1Origins][];
        l1Origins[numberOfL1Origins - 1] = lastL1Origin;
        ReceiptForRpc[]? lastReceipts = await _l1Bridge.GetReceiptsByBlockHash(lastL1Origin.Hash);
        ArgumentNullException.ThrowIfNull(lastReceipts);
        l1Receipts[numberOfL1Origins - 1] = lastReceipts;
        Hash256 parentHash = lastL1Origin.ParentHash;
        for (ulong i = 1; i < numberOfL1Origins; i++)
        {
            // TODO: try to save time here
            BlockForRpc? l1Origin = await _l1Bridge.GetBlockByHash(parentHash);
            ReceiptForRpc[]? receipts = await _l1Bridge.GetReceiptsByBlockHash(parentHash);
            ArgumentNullException.ThrowIfNull(l1Origin);
            ArgumentNullException.ThrowIfNull(receipts);
            l1Origins[numberOfL1Origins - i - 1] = l1Origin;
            l1Receipts[numberOfL1Origins - i - 1] = receipts;
            parentHash = l1Origin.ParentHash;
        }

        var pa = _payloadAttributesDeriver.DerivePayloadAttributes(batch, l2Parent, l1Origins, l1Receipts);

        _logger.Error($"Processed batch RelTimestamp: {batch.RelTimestamp}");
        return (pa.Item1, pa.Item2, pa.Item3);
    }

    private ulong GetNumberOfBits(BigInteger number)
    {
        ulong cnt = 0;
        for (int i = 0; i < number.GetBitLength(); ++i)
        {
            if (((number >> i) & 1) == 1)
            {
                cnt++;
            }
        }
        return cnt;
    }

    public Task ConsumeV0Batches(BatchV0[] batches)
    {
        throw new NotImplementedException();
    }
}
