// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Queues a forkchoice head for processing again when it is in the tree on a processed parent but was never processed.</summary>
/// <remarks>
/// A block leaves the processing queue without a verdict when its processing throws or runs past
/// <c>Blocks.BlockProcessingTimeoutMs</c>. A forkchoice update alone would otherwise leave it there: suggesting a
/// known block again does nothing, and without a beacon pivot sync does not start. Blocks inserted by beacon sync,
/// including a newPayload child of such a block, are left to sync. Each SYNCING answer that finds the queue empty
/// retries, as a re-sent newPayload would; at most one retry is in flight.
/// </remarks>
internal sealed class ReprocessingForkchoiceUpdatedHandler(
    IForkchoiceUpdatedHandler inner,
    IBlockTree blockTree,
    IStateReader stateReader,
    IBlockProcessingQueue processingQueue,
    IReceiptConfig receiptConfig,
    ILogManager logManager) : IForkchoiceUpdatedHandler
{
    private readonly ProcessingOptions _processingOptions = receiptConfig.StoreReceipts ? ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts : ProcessingOptions.EthereumMerge;
    private readonly ILogger _logger = logManager.GetClassLogger<ReprocessingForkchoiceUpdatedHandler>();
    private Task _reprocessing = Task.CompletedTask;

    public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Handle(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
    {
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await inner.Handle(forkchoiceState, payloadAttributes, version);
        if (result.Data?.PayloadStatus.Status is PayloadStatus.Syncing && _reprocessing.IsCompleted && processingQueue.Count == 0)
        {
            TryReprocess(forkchoiceState.HeadBlockHash);
        }

        return result;
    }

    private void TryReprocess(Hash256 headHash)
    {
        // FindBlock goes last: without block info it would create the level.
        if (blockTree.FindHeader(headHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) is not { ParentHash: not null } header
            || blockTree.GetInfo(header.Number, headHash).Info is not { WasProcessed: false, IsBeaconInfo: false }
            || blockTree.FindHeader(header.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded, header.Number - 1) is not { } parent
            || !blockTree.WasProcessed(parent.Number, parent.GetOrCalculateHash())
            || !stateReader.HasStateForBlock(parent)
            || blockTree.FindBlock(headHash, BlockTreeLookupOptions.None, header.Number) is not { } head)
        {
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Processing head {head.ToString(Block.Format.Short)} again: it was left unprocessed on a processed parent.");
        // Off this thread: with the queue empty the processing channel's synchronous continuations would run the whole
        // block inside Enqueue, on this thread, while it holds the engine API's lock (see NewPayloadHandler).
        _reprocessing = Task.Run(() => EnqueueAsync(head));
    }

    private async Task EnqueueAsync(Block block)
    {
        try
        {
            await processingQueue.Enqueue(block, _processingOptions);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Queueing head {block.ToString(Block.Format.Short)} for processing again failed: {e}");
        }
    }
}
