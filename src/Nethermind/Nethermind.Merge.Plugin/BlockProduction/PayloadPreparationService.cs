// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
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
public class PayloadPreparationService : IPayloadPreparationService, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly PostMergeBlockProducer _blockProducer;
    private readonly IBlockImprovementContextFactory _blockImprovementContextFactory;
    private readonly ILogger _logger;
    private readonly Core.Timers.ITimer _timer;

    // by default we will cleanup the old payload once per six slot. There is no need to fire it more often
    public const int SlotsPerOldPayloadCleanup = 6;
    public static readonly TimeSpan GetPayloadWaitForNonEmptyBlockMillisecondsDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Delay between block improvements
    /// </summary>
    private readonly TimeSpan? _improvementDelay;

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
        _improvementDelay = improvementDelay;
        _timer = timerFactory.CreateTimer(slotsPerOldPayloadCleanup * timeout);
        _timer.Elapsed += CleanupOldPayloads;
        _timer.Start();

        _logger = logManager.GetClassLogger();
    }

    public string StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
    {
        string payloadId = payloadAttributes.GetPayloadId(parentHeader);
        if (!_payloadStorage.ContainsKey(payloadId))
        {
            if (_logger.IsInfo) _logger.Info($" Production Request  {parentHeader.Number + 1} PayloadId: {payloadId}");
            long startTimestamp = Stopwatch.GetTimestamp();
            Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes);
            if (_logger.IsInfo) _logger.Info($" Produced (Empty)    {emptyBlock.ToString(emptyBlock.Difficulty != 0 ? Block.Format.HashNumberDiffAndTx : Block.Format.HashNumberMGasAndTx)} | {Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,8:N2} ms");
            ImproveBlock(payloadId, parentHeader, payloadAttributes, emptyBlock, DateTimeOffset.UtcNow, default, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        }
        else if (_logger.IsInfo)
        {
            // Shouldn't really happen so move string construction code out of hot method
            LogMultiStartRequest(payloadId, parentHeader.Number + 1);
        }

        return payloadId;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogMultiStartRequest(string payloadId, long number)
        {
            _logger.Info($"Payload for block {number} with same parameters has already started. PayloadId: {payloadId}");
        }
    }

    protected virtual Block ProduceEmptyBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes)
    {
        bool isTrace = _logger.IsTrace;
        if (isTrace) TraceBefore(payloadId, parentHeader);

        Block emptyBlock = _blockProducer.PrepareEmptyBlock(parentHeader, payloadAttributes);

        if (isTrace) TraceAfter(payloadId, emptyBlock);
        return emptyBlock;

        // Rarely in Trace so move string construction code out of hot method
        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceBefore(string payloadId, BlockHeader parentHeader)
            => _logger.Trace($"Preparing empty block from payload {payloadId} with parent {parentHeader}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceAfter(string payloadId, Block emptyBlock)
            => _logger.Trace($"Prepared empty block from payload {payloadId} block: {emptyBlock}");
    }

    protected virtual void ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, CancellationTokenSource cts) =>
        _payloadStorage.AddOrUpdate(payloadId,
            id => CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentBlockFees, cts),
            (id, currentContext) =>
            {
                if (cts.IsCancellationRequested)
                {
                    // If cancelled, return previous
                    if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, improvement has been cancelled");
                    return currentContext;
                }
                if (!currentContext.ImprovementTask.IsCompleted)
                {
                    // If there is payload improvement and its not yet finished leave it be
                    if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, previous improvement hasn't finished");
                    return currentContext;
                }

                IBlockImprovementContext newContext = CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentContext.BlockFees, cts);
                if (!cts.IsCancellationRequested)
                {
                    currentContext.Dispose();
                    return newContext;
                }
                else
                {
                    newContext.Dispose();
                    return currentContext;
                }
            });


    private IBlockImprovementContext CreateBlockImprovementContext(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, CancellationTokenSource cts)
    {
        if (_logger.IsTrace) _logger.Trace($"Start improving block from payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");

        long startTimestamp = Stopwatch.GetTimestamp();
        long added = Volatile.Read(ref TxPool.Metrics.PendingTransactionsAdded);
        IBlockImprovementContext blockImprovementContext = _blockImprovementContextFactory.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime, currentBlockFees, cts);
        blockImprovementContext.ImprovementTask.ContinueWith(
            (b) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    LogProductionResult(b, currentBestBlock, blockImprovementContext.BlockFees, Stopwatch.GetElapsedTime(startTimestamp));
                }
            },
            TaskContinuationOptions.RunContinuationsAsynchronously);
        blockImprovementContext.ImprovementTask.ContinueWith(async b =>
        {
            CancellationToken token = cts.Token;
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                TimeSpan dynamicDelay = CalculateImprovementDelay(startDateTime, startTimestamp);
                if (dynamicDelay == Timeout.InfiniteTimeSpan)
                {
                    // If we can't finish before that cutoff, skip the improvement
                    if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, no more time in slot");
                    return;
                }

                // If we reach here, we still have time for an improvement build (which still responds to cancellation)
                try
                {
                    await Task.Delay(dynamicDelay, token);
                }
                catch (OperationCanceledException) { }

                // Loop the delay if no new txs have been added, and while not cancelled.
                // Is no point in rebuilding an identical block.
            } while (added == Volatile.Read(ref TxPool.Metrics.PendingTransactionsAdded));

            if (!token.IsCancellationRequested || !blockImprovementContext.Disposed) // if GetPayload wasn't called for this item or it wasn't cleared
            {
                Block newBestBlock = blockImprovementContext.CurrentBestBlock ?? currentBestBlock;
                ImproveBlock(payloadId, parentHeader, payloadAttributes, newBestBlock, startDateTime, blockImprovementContext.BlockFees, cts);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, it was retrieved");
            }
        });

        return blockImprovementContext;
    }

    private TimeSpan CalculateImprovementDelay(DateTimeOffset startDateTime, long startTimestamp)
    {
        // We want to keep building better blocks throughout this slot so that when 
        // the consensus client requests the block, we have the best version ready.
        // However, building blocks repeatedly is expensive. We also expect more 
        // transactions towards the end of the slot, when it's more likely we will 
        // actually need to deliver the block. So we slow down improvements early in 
        // the slot (to save resources) and gradually increase the improvement 
        // frequency toward the end of the slot.
        //
        // This is both to capture more transactions, and where the probability of
        // being asked for a block is highest; so where the improvements will
        // likely have highest impact.

        TimeSpan dynamicDelay;
        if (!_improvementDelay.HasValue)
        {
            // Calculate how much time is left in the slot:
            TimeSpan timeUsedInSlot = DateTimeOffset.UtcNow - startDateTime;
            TimeSpan timeRemainingInSlot = _timePerSlot - timeUsedInSlot;

            // Ensure we always have at least a tiny delay to avoid spinning too fast.
            TimeSpan minDelay = TimeSpan.FromMilliseconds(1);
            if (timeRemainingInSlot < minDelay)
            {
                timeRemainingInSlot = minDelay;
            }

            // progress = 0  => at the very start of the slot
            // progress = 1  => at the very end of the slot
            double progress = timeUsedInSlot.TotalSeconds / _timePerSlot.TotalSeconds;

            // Clamp progress to [0, 1] just in case:
            progress = Math.Clamp(progress, 0.0, 1.0);

            // We use a cubic curve (progress^3) so that improvement builds 
            // start off quite spaced out (less frequent at the start) and 
            // rapidly become more frequent as we near the end of the slot.
            progress *= progress * progress;

            // We'll interpolate between two fractional rates:
            // - fractionStart (1/6): a slower build rate at the start
            // - fractionEnd   (1/960): a faster build rate near the end
            const double fractionStart = 1.0 / 6.0;
            const double fractionEnd = 1.0 / 960.0;
            // Slot Timeline: 0% -------------------------- 100%
            //                |        (long gap)         | 
            //    [Block Improvement #1]        <--- big delay here
            // 
            //                                   (medium gap)
            //                                       [Block Improvement #2]
            //                                           (small gap)
            //                                              [Block Improvement #3]
            //                                                (tiny gap)
            //                                                   [Block Improvement #4]
            //
            double currentFraction = fractionStart + (fractionEnd - fractionStart) * progress;
            // Dynamic delay = currentFraction * (remaining slot time)
            // So near the start: delay is bigger (slower improvement)
            // Near the end: delay shrinks, allowing more frequent improvements
            dynamicDelay = TimeSpan.FromSeconds(timeRemainingInSlot.TotalSeconds * currentFraction);
            if (dynamicDelay < minDelay)
            {
                // Don't want to spin endlessly if no new txs
                dynamicDelay = minDelay;
            }
        }
        else
        {
            // In tests, we override the dynamic strategy with a fixed delay
            dynamicDelay = _improvementDelay.Value;
        }

        TimeSpan lastBuildTime = Stopwatch.GetElapsedTime(startTimestamp);
        DateTimeOffset whenWeCouldFinishNextProduction = DateTimeOffset.UtcNow + dynamicDelay + lastBuildTime;
        // We don't want to keep improving a block too far beyond the slot duration.
        // Specifically, we allow ourselves at most 30% extra of the nominal slot time.
        // This is just a break in case the improvement is never cancelled.
        DateTimeOffset slotPlusThirdFinishTime = startDateTime + _timePerSlot * 1.3;
        if (whenWeCouldFinishNextProduction > slotPlusThirdFinishTime)
        {
            // If we can't finish before that cutoff, skip the improvement
            dynamicDelay = Timeout.InfiniteTimeSpan;
        }

        return dynamicDelay;
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
        const long weiToGwei = 1_000_000_000;

        if (t.IsCompletedSuccessfully)
        {
            Block? block = t.Result;
            if (block is not null && !ReferenceEquals(block, currentBestBlock))
            {
                bool supportsBlobs = _blockProducer.SupportsBlobs;
                int blobs = 0;
                int blobTx = 0;
                UInt256 gas = 0;
                if (supportsBlobs)
                {
                    foreach (Transaction tx in block.Transactions)
                    {
                        int blobCount = tx.GetBlobCount();
                        if (blobCount > 0)
                        {
                            blobs += blobCount;
                            blobTx++;
                            tx.TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 premiumPerGas);
                            gas += (ulong)tx.SpentGas * premiumPerGas;
                        }
                    }
                }
                _logger.Info($" Produced {blockFees.ToDecimal(null) / weiToEth,6:N4}{BlocksConfig.GasTokenTicker,4} {block.ToString(block.Difficulty != 0 ? Block.Format.HashNumberDiffAndTx : Block.Format.HashNumberMGasAndTx)} | {time.TotalMilliseconds,8:N2} ms {((supportsBlobs && blobs > 0) ? $"{blobs,2:N0} blobs in {blobTx,2:N0} tx @ {(decimal)gas / weiToGwei,7:N0} gwei" : "")}");
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

    public async ValueTask<IBlockProductionContext?> GetPayload(string payloadId, bool skipCancel = false)
    {
        if (_payloadStorage.TryGetValue(payloadId, out IBlockImprovementContext? blockContext))
        {
            using (blockContext)
            {
                bool currentBestBlockIsEmpty = blockContext.CurrentBestBlock?.Transactions.Length == 0;
                if (currentBestBlockIsEmpty && !blockContext.ImprovementTask.IsCompleted)
                {
                    // Inform current improvement that we need results now
                    if (!skipCancel)
                    {
                        blockContext.CancelOngoingImprovements();

                        using CancellationTokenSource cts = new();
                        Task timeout = Task.Delay(GetPayloadWaitForNonEmptyBlockMillisecondsDelay, cts.Token);
                        Task completedTask = await Task.WhenAny(blockContext.ImprovementTask, timeout);
                        if (completedTask != timeout)
                        {
                            cts.Cancel();
                        }
                    }
                    else
                    {
                        await blockContext.ImprovementTask;
                    }
                }
                else
                {
                    // Stop any on-going improvements as they won't be used
                    blockContext.CancelOngoingImprovements();
                }

                return blockContext;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _shutdown.Cancel();
    }

    public void CancelBlockProductionForParent(object? sender, BlockHeader parentHeader)
    {
        PayloadAttributes payloadAttributes = parentHeader.GenerateSimulatedPayload();
        string payloadId = payloadAttributes.GetPayloadId(parentHeader);
        // GetPayload cancels the request
        _ = GetPayload(payloadId);
    }
}
