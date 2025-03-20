// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.BlockProduction;

/// <summary>
/// A cache of pending payloads. A payload is created whenever a consensus client requests a payload creation in <see cref="ForkchoiceUpdatedHandler"/>.
/// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_forkchoiceupdatedv1"/>
/// Each payload is assigned a payloadId which can be used by the consensus client to retrieve payload later by calling a <see cref="GetPayloadV1Handler"/>.
/// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_getpayloadv1"/>
/// </summary>
public class PayloadPreparationService : IPayloadPreparationService
{
    private readonly PostMergeBlockProducer _blockProducer;
    private readonly IBlockImprovementContextFactory _blockImprovementContextFactory;
    private readonly ILogger _logger;

    // by default we will cleanup the old payload once per six slot. There is no need to fire it more often
    public const int SlotsPerOldPayloadCleanup = 6;
    public static readonly TimeSpan GetPayloadWaitForFullBlockMillisecondsDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultImprovementDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Delay between block improvements
    /// </summary>
    private readonly TimeSpan _improvementDelay;

    private readonly TimeSpan _cleanupOldPayloadDelay;
    private readonly TimeSpan _timePerSlot;

    // first ExecutionPayloadV1 is empty (without txs), second one is the ideal one
    protected readonly ConcurrentDictionary<string, IBlockImprovementContext> _payloadStorage = new();

    public PayloadPreparationService(
        PostMergeBlockProducer blockProducer,
        IBlockImprovementContextFactory blockImprovementContextFactory,
        ITimerFactory timerFactory,
        ILogManager logManager,
        TimeSpan timePerSlot,
        int slotsPerOldPayloadCleanup = SlotsPerOldPayloadCleanup,
        TimeSpan? improvementDelay = null)
    {
        _blockProducer = blockProducer;
        _blockImprovementContextFactory = blockImprovementContextFactory;
        _timePerSlot = timePerSlot;
        TimeSpan timeout = timePerSlot;
        _cleanupOldPayloadDelay = 3 * timePerSlot; // 3 * slots time
        _improvementDelay = improvementDelay ?? DefaultImprovementDelay;
        ITimer timer = timerFactory.CreateTimer(slotsPerOldPayloadCleanup * timeout);
        timer.Elapsed += CleanupOldPayloads;
        timer.Start();

        _logger = logManager.GetClassLogger();
    }

    public string StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
    {
        string payloadId = payloadAttributes.GetPayloadId(parentHeader);
        if (!_payloadStorage.ContainsKey(payloadId))
        {
            Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes);
            ImproveBlock(payloadId, parentHeader, payloadAttributes, emptyBlock, DateTimeOffset.UtcNow, default);
        }
        else if (_logger.IsInfo) _logger.Info($"Payload with the same parameters has already started. PayloadId: {payloadId}");

        return payloadId;
    }

    protected virtual Block ProduceEmptyBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes)
    {
        if (_logger.IsTrace) _logger.Trace($"Preparing empty block from payload {payloadId} with parent {parentHeader}");
        Block emptyBlock = _blockProducer.PrepareEmptyBlock(parentHeader, payloadAttributes);
        if (_logger.IsTrace) _logger.Trace($"Prepared empty block from payload {payloadId} block: {emptyBlock}");
        return emptyBlock;
    }

    protected virtual void ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees) =>
        _payloadStorage.AddOrUpdate(payloadId,
            id => CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentBlockFees),
            (id, currentContext) =>
            {
                // if there is payload improvement and its not yet finished leave it be
                if (!currentContext.ImprovementTask.IsCompleted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, previous improvement hasn't finished");
                    return currentContext;
                }

                IBlockImprovementContext newContext = CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentContext.BlockFees);
                currentContext.Dispose();
                return newContext;
            });


    private IBlockImprovementContext CreateBlockImprovementContext(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees)
    {
        if (_logger.IsTrace) _logger.Trace($"Start improving block from payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");

        long startTimestamp = Stopwatch.GetTimestamp();
        IBlockImprovementContext blockImprovementContext = _blockImprovementContextFactory.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime, currentBlockFees);
        blockImprovementContext.ImprovementTask.ContinueWith(
            (b) => LogProductionResult(b, currentBestBlock, blockImprovementContext.BlockFees, Stopwatch.GetElapsedTime(startTimestamp)),
            TaskContinuationOptions.RunContinuationsAsynchronously);
        blockImprovementContext.ImprovementTask.ContinueWith(async _ =>
        {
            // if after delay we still have time to try producing the block in this slot
            TimeSpan lastBuildTime = Stopwatch.GetElapsedTime(startTimestamp);
            DateTimeOffset whenWeCouldFinishNextProduction = DateTimeOffset.UtcNow + _improvementDelay + lastBuildTime;
            DateTimeOffset slotFinished = startDateTime + _timePerSlot;
            if (whenWeCouldFinishNextProduction < slotFinished)
            {
                if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} will be improved in {_improvementDelay.TotalMilliseconds}ms");
                await Task.Delay(_improvementDelay);
                if (!blockImprovementContext.Disposed) // if GetPayload wasn't called for this item or it wasn't cleared
                {
                    Block newBestBlock = blockImprovementContext.CurrentBestBlock ?? currentBestBlock;
                    ImproveBlock(payloadId, parentHeader, payloadAttributes, newBestBlock, startDateTime, blockImprovementContext.BlockFees);
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, it was retrieved");
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, no more time in slot");
            }
        });

        return blockImprovementContext;
    }

    private void CleanupOldPayloads(object? sender, EventArgs e)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace("Started old payloads cleanup");
            foreach (KeyValuePair<string, IBlockImprovementContext> payload in _payloadStorage)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (payload.Value.StartDateTime + _cleanupOldPayloadDelay <= now)
                {
                    if (_logger.IsDebug) _logger.Info($"A new payload to remove: {payload.Key}, Current time {now:t}, Payload timestamp: {payload.Value.CurrentBestBlock?.Timestamp}");

                    if (_payloadStorage.TryRemove(payload.Key, out IBlockImprovementContext? context))
                    {
                        context.Dispose();
                        if (_logger.IsDebug) _logger.Info($"Cleaned up payload with id={payload.Key} as it was not requested");
                    }
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Finished old payloads cleanup");
        }
        catch (Exception ex)
        {
            _logger.Error($"Exception in old payloads cleanup: {ex}");
        }

    }

    private void LogProductionResult(Task<Block?> t, Block currentBestBlock, UInt256 blockFees, TimeSpan time)
    {
        const long weiToEth = 1_000_000_000_000_000_000;

        if (t.IsCompletedSuccessfully)
        {
            Block? block = t.Result;
            if (block is not null && !ReferenceEquals(block, currentBestBlock))
            {
                bool supportsBlobs = _blockProducer.SupportsBlobs;
                int blobs = 0;
                if (supportsBlobs)
                {
                    foreach (Transaction tx in block.Transactions)
                    {
                        blobs += tx.BlobVersionedHashes?.Length ?? 0;
                    }
                }
                _logger.Info($" Produced  {blockFees.ToDecimal(null) / weiToEth,5:N3}{BlocksConfig.GasTokenTicker,4} {block.ToString(block.Difficulty != 0 ? Block.Format.HashNumberDiffAndTx : Block.Format.HashNumberMGasAndTx)} | {time.TotalMilliseconds,9:N2} ms | {(supportsBlobs ? $"blobs {blobs,4:N0}" : "")}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug(" Block improvement attempt did not produce a better block");
            }
        }
        else if (t.IsFaulted)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Block improvement failed", t.Exception);
        }
        else if (t.IsCanceled)
        {
            if (_logger.IsDebug) _logger.Debug("Block improvement was canceled");
        }
    }

    public async ValueTask<IBlockProductionContext?> GetPayload(string payloadId)
    {
        if (_payloadStorage.TryGetValue(payloadId, out IBlockImprovementContext? blockContext))
        {
            using (blockContext)
            {
                bool currentBestBlockIsEmpty = blockContext.CurrentBestBlock?.Transactions.Length == 0;
                if (currentBestBlockIsEmpty && !blockContext.ImprovementTask.IsCompleted)
                {
                    await Task.WhenAny(blockContext.ImprovementTask, Task.Delay(GetPayloadWaitForFullBlockMillisecondsDelay));
                }

                return blockContext;
            }
        }

        return null;
    }
}
