// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.L1Bridge;

namespace Nethermind.Optimism.CL.Derivation;

public class DerivationPipeline(
    IPayloadAttributesDeriver payloadAttributesDeriver,
    IL1Bridge l1Bridge,
    ulong l2GenesisTimestamp,
    ulong l2BlockTime,
    ulong chainId,
    ILogger logger) : IDerivationPipeline
{

    public async Task<PayloadAttributesRef[]> DerivePayloadAttributes(L2Block l2Parent, BatchV1 batch, CancellationToken token)
    {
        // TODO: propagate CancellationToken
        if (logger.IsInfo) logger.Info($"Processing batch RelTimestamp: {batch.RelTimestamp}");
        ulong expectedParentNumber = batch.RelTimestamp / 2 - 1;
        ArgumentNullException.ThrowIfNull(l2Parent);
        if (expectedParentNumber != l2Parent.Number)
        {
            throw new ArgumentException("Old batch");
        }

        (L1Block[]? l1Origins, ReceiptForRpc[][]? l1Receipts) = await GetL1Origins(batch, token);
        if (l1Origins is null || l1Receipts is null)
        {
            if (logger.IsWarn) logger.Warn($"Unable to get L1 Origins for span batch. RelTimestamp: {batch.RelTimestamp}");
            throw new Exception("Unable to get L1 Origins");
        }

        PayloadAttributesRef l2ParentPayloadAttributes = new()
        {
            L1BlockInfo = l2Parent.L1BlockInfo,
            Number = l2Parent.Number,
            PayloadAttributes = l2Parent.PayloadAttributes,
            SystemConfig = l2Parent.SystemConfig
        };
        List<PayloadAttributesRef> result = new((int)batch.BlockCount);
        int originIdx = 0;
        try
        {
            foreach (var singularBatch in batch.ToSingularBatches(chainId, l2GenesisTimestamp, l2BlockTime))
            {
                if (singularBatch.IsFirstBlockInEpoch) originIdx++;
                var payloadAttributes = payloadAttributesDeriver.TryDerivePayloadAttributes(
                    singularBatch,
                    l2ParentPayloadAttributes,
                    l1Origins[originIdx],
                    l1Receipts[originIdx]);
                if (payloadAttributes is null)
                {
                    if (logger.IsWarn) logger.Warn($"Unable to derive payload attributes. Batch timestamp: {singularBatch.Timestamp}");
                    return result.ToArray();
                }
                result.Add(payloadAttributes);

                l2ParentPayloadAttributes = payloadAttributes;
            }
        }
        catch (Exception e)
        {
            if (logger.IsError) logger.Error($"Exception occured while processing batch. RelTimestamp: {batch.RelTimestamp}, {e.Message}, {e.StackTrace}");
            throw;
        }

        if (logger.IsInfo) logger.Info($"Processed batch RelTimestamp: {batch.RelTimestamp}, Number of payload attributes: {result.Count}");
        return result.ToArray();
    }

    private async Task<(L1Block[]?, ReceiptForRpc[][]?)> GetL1Origins(BatchV1 batch, CancellationToken token)
    {
        ulong numberOfL1Origins = GetNumberOfBits(batch.OriginBits) + 1;
        ulong lastL1OriginNum = batch.L1OriginNum;
        L1Block lastL1Origin = await l1Bridge.GetBlock(lastL1OriginNum, token);
        if (!lastL1Origin.Hash.Bytes.StartsWith(batch.L1OriginCheck))
        {
            logger.Warn($"Batch with invalid origin. Expected {batch.L1OriginCheck.ToHexString()}, Got {lastL1Origin.Hash}");
            return (null, null);
        }
        var l1Origins = new L1Block[numberOfL1Origins];
        var l1Receipts = new ReceiptForRpc[numberOfL1Origins][];
        l1Origins[numberOfL1Origins - 1] = lastL1Origin;
        ReceiptForRpc[] lastReceipts = await l1Bridge.GetReceiptsByBlockHash(lastL1Origin.Hash, token);
        l1Receipts[numberOfL1Origins - 1] = lastReceipts;
        Hash256 parentHash = lastL1Origin.ParentHash;
        for (ulong i = 1; i < numberOfL1Origins; i++)
        {
            L1Block l1Origin = await l1Bridge.GetBlockByHash(parentHash, token);
            ReceiptForRpc[] receipts = await l1Bridge.GetReceiptsByBlockHash(parentHash, token);
            l1Origins[numberOfL1Origins - i - 1] = l1Origin;
            l1Receipts[numberOfL1Origins - i - 1] = receipts;
            parentHash = l1Origin.ParentHash;
        }
        return (l1Origins, l1Receipts);
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
}
