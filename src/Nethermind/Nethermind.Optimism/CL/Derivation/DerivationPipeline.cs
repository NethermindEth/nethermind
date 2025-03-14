// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.L1Bridge;

namespace Nethermind.Optimism.CL.Derivation;

public class DerivationPipeline : IDerivationPipeline
{
    private readonly IPayloadAttributesDeriver _payloadAttributesDeriver;
    private readonly IL1Bridge _l1Bridge;
    private readonly IExecutionEngineManager _executionEngineManager;
    private readonly ILogger _logger;

    public DerivationPipeline(
        IPayloadAttributesDeriver payloadAttributesDeriver,
        IL1Bridge l1Bridge,
        IExecutionEngineManager executionEngineManager,
        ILogger logger)
    {
        _payloadAttributesDeriver = payloadAttributesDeriver;
        _l1Bridge = l1Bridge;
        _executionEngineManager = executionEngineManager;
        _logger = logger;
    }

    public async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            (L2Block l2Parent, BatchV1 batch) = await _inputChannel.Reader.ReadAsync(token);
            _logger.Error($"Got batch for derivation: {batch.RelTimestamp / 2 - 1}");
            await ProcessOneBatch(l2Parent, batch);
        }
    }

    private readonly Channel<(L2Block L2Parent, BatchV1 Batch)> _inputChannel = Channel.CreateBounded<(L2Block, BatchV1)>(20);
    public ChannelWriter<(L2Block L2Parent, BatchV1 Batch)> BatchesForProcessing => _inputChannel.Writer;

    private async Task ProcessOneBatch(L2Block l2Parent, BatchV1 batch)
    {
        _logger.Error($"Processing batch RelTimestamp: {batch.RelTimestamp}");
        ulong expectedParentNumber = batch.RelTimestamp / 2 - 1;
        ArgumentNullException.ThrowIfNull(l2Parent);
        if (expectedParentNumber != l2Parent.Number)
        {
            throw new ArgumentException("Old batch");
        }

        // TODO: proper error handling
        ulong numberOfL1Origins = GetNumberOfBits(batch.OriginBits) + 1;
        ulong lastL1OriginNum = batch.L1OriginNum;
        L1Block? lastL1Origin = await _l1Bridge.GetBlock(lastL1OriginNum);
        ArgumentNullException.ThrowIfNull(lastL1Origin);
        if (!lastL1Origin.Value.Hash.Bytes.StartsWith(batch.L1OriginCheck))
        {
            // TODO: potential L1 reorg
            throw new ArgumentException("Invalid batch origin");
        }
        L1Block[] l1Origins = new L1Block[numberOfL1Origins];
        ReceiptForRpc[][] l1Receipts = new ReceiptForRpc[numberOfL1Origins][];
        l1Origins[numberOfL1Origins - 1] = lastL1Origin.Value;
        ReceiptForRpc[]? lastReceipts = await _l1Bridge.GetReceiptsByBlockHash(lastL1Origin.Value.Hash);
        ArgumentNullException.ThrowIfNull(lastReceipts);
        l1Receipts[numberOfL1Origins - 1] = lastReceipts;
        Hash256 parentHash = lastL1Origin.Value.ParentHash;
        for (ulong i = 1; i < numberOfL1Origins; i++)
        {
            // TODO: try to save time here
            L1Block? l1Origin = await _l1Bridge.GetBlockByHash(parentHash);
            ReceiptForRpc[] receipts = await _l1Bridge.GetReceiptsByBlockHash(parentHash);
            ArgumentNullException.ThrowIfNull(l1Origin);
            ArgumentNullException.ThrowIfNull(receipts);
            l1Origins[numberOfL1Origins - i - 1] = l1Origin.Value;
            l1Receipts[numberOfL1Origins - i - 1] = receipts;
            parentHash = l1Origin.Value.ParentHash;
        }

        PayloadAttributesRef l2ParentPayloadAttributes = new()
        {
            L1BlockInfo = l2Parent.L1BlockInfo,
            Number = l2Parent.Number,
            PayloadAttributes = l2Parent.PayloadAttributes,
            SystemConfig = l2Parent.SystemConfig
        };
        int originIdx = 0;
        try
        {
            foreach (var singularBatch in batch.ToSingularBatches(480, 1719335639, 2))
            {
                if (singularBatch.IsFirstBlockInEpoch) originIdx++;
                var payloadAttributes = _payloadAttributesDeriver.DerivePayloadAttributes(
                    singularBatch,
                    l2ParentPayloadAttributes,
                    l1Origins[originIdx],
                    l1Receipts[originIdx]);
                l2ParentPayloadAttributes = payloadAttributes;
                bool valid = await _executionEngineManager.ProcessNewDerivedPayloadAttributes(payloadAttributes);
                if (!valid)
                {
                    _logger.Warn($"Derived invalid payload attributes. Number: {payloadAttributes.Number}.");
                    return; // Holocene: Drop everything that comes after invalid batch
                }
            }
        }
        catch (Exception e)
        {
            _logger.Error($"Exception occured while processing batch RelTimestamp: {batch.RelTimestamp}, {e.Message}, {e.StackTrace}");
            throw;
        }

        _logger.Error($"Processed batch RelTimestamp: {batch.RelTimestamp}");
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
