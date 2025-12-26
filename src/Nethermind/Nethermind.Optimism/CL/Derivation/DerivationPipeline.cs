// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    ILogManager logManager) : IDerivationPipeline
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public async IAsyncEnumerable<PayloadAttributesRef> DerivePayloadAttributes(L2Block l2Parent, BatchV1 batch,
        [EnumeratorCancellation] CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(l2Parent);
        ulong firstBlockNumber = batch.RelTimestamp / l2BlockTime;
        if (_logger.IsInfo) _logger.Info($"Processing batch. Block numbers from {firstBlockNumber} to {firstBlockNumber + batch.BlockCount - 1}");
        if (firstBlockNumber - 1 != l2Parent.Number)
        {
            throw new ArgumentException("Old batch");
        }

        (L1Block[]? l1Origins, ReceiptForRpc[][]? l1Receipts) = await GetL1Origins(batch, token);
        if (l1Origins is null || l1Receipts is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to get L1 Origins for span batch. RelTimestamp: {batch.RelTimestamp}");
            throw new Exception("Unable to get L1 Origins");
        }

        PayloadAttributesRef l2ParentPayloadAttributes = new()
        {
            L1BlockInfo = l2Parent.L1BlockInfo,
            Number = l2Parent.Number,
            PayloadAttributes = l2Parent.PayloadAttributes,
            SystemConfig = l2Parent.SystemConfig
        };
        int originIdx = 0;
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
                if (_logger.IsWarn)
                    _logger.Warn($"Unable to derive payload attributes. Batch timestamp: {singularBatch.Timestamp}");
                yield break;
            }

            yield return payloadAttributes;

            l2ParentPayloadAttributes = payloadAttributes;
        }
    }

    private async Task<(L1Block[]?, ReceiptForRpc[][]?)> GetL1Origins(BatchV1 batch, CancellationToken token)
    {
        ulong numberOfL1Origins = (ulong)BigInteger.PopCount(batch.OriginBits) + 1;
        ulong lastL1OriginNum = batch.L1OriginNum;
        L1Block lastL1Origin = await l1Bridge.GetBlock(lastL1OriginNum, token);
        if (!lastL1Origin.Hash.Bytes.StartsWith(batch.L1OriginCheck.Span))
        {
            _logger.Warn($"Batch with invalid origin. Expected {batch.L1OriginCheck.ToHexString()}, Got {lastL1Origin.Hash}");
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
}
