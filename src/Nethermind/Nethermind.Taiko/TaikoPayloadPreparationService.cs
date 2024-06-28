// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Taiko;

public class TaikoPayloadPreparationService(
    PostMergeBlockProducer blockProducer,
    ITimerFactory timerFactory,
    ILogManager logManager,
    TimeSpan timePerSlot,
    IL1OriginStore l1OriginStore) : PayloadPreparationService(
        blockProducer,
        NoBlockImprovementContextFactory.Instance,
        timerFactory,
        logManager,
        timePerSlot)
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly IL1OriginStore _l1OriginStore = l1OriginStore;

    protected override void ImproveBlock(string payloadId, BlockHeader parentHeader,
        PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime)
    {
        TaikoPayloadAttributes attrs = (payloadAttributes as TaikoPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes have incorrect type. Expected TaikoPayloadAttributes.");

        // L1Origin **MUST NOT** be null, it's a required field in PayloadAttributes.
        L1Origin l1Origin = attrs.L1Origin ?? throw new InvalidOperationException("L1Origin is required");

        // Set the block hash before inserting the L1Origin into database.
        l1Origin.L2BlockHash = currentBestBlock.Hash;

        // Write L1Origin.
        _l1OriginStore.WriteL1Origin(l1Origin.BlockID, l1Origin);
        // Write the head L1Origin.
        _l1OriginStore.WriteHeadL1Origin(l1Origin.BlockID);

        // ignore TryAdd failure (it can only happen if payloadId is already in the dictionary)
        _payloadStorage.TryAdd(payloadId,
            new NoBlockImprovementContext(currentBestBlock, UInt256.Zero, startDateTime));
    }
}
