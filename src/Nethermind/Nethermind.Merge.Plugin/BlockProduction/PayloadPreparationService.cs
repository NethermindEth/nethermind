// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.BlockProduction;

using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
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
    public static readonly TimeSpan DefaultImprovementDelay = TimeSpan.FromMilliseconds(3000);

    /// <summary>
    /// Delay between block improvements
    /// </summary>
    private readonly TimeSpan _improvementDelay;

    private readonly TimeSpan _cleanupOldPayloadDelay;
    private readonly TimeSpan _timePerSlot;
    private CancellationTokenSource _tokenSource = new();
    TaskCompletionSource _newPendingTxWaiter = new TaskCompletionSource();
    private bool _isDisposed;

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

        if (blockProducer.SupportsNotifications)
        {
            blockProducer.NewPendingTransactions += BlockProducer_NewPendingTransactions;
        }

        _logger = logManager.GetClassLogger();
    }

    private BlockHeader? _currentParent;

    private void BlockProducer_NewPendingTransactions(object? sender, TxPool.TxEventArgs e)
    {
        // Ignore tx if gas is too low to run
        if (_currentParent is null || _blockProducer.IsInterestingTx(e.Transaction, _currentParent))
        {
            _newPendingTxWaiter.TrySetResult();
        }
    }

    public string StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
    {
        string payloadId = payloadAttributes.GetPayloadId(parentHeader);
        if (!_isDisposed && !_payloadStorage.ContainsKey(payloadId))
        {
            CancellationTokenSource tokenSource = CancelOngoingImprovements();
            _currentParent = parentHeader;
            Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes, tokenSource.Token);
            ImproveBlock(payloadId, parentHeader, payloadAttributes, emptyBlock, DateTimeOffset.UtcNow, default, _tokenSource.Token);
        }
        else if (_logger.IsInfo) _logger.Info($"Payload with the same parameters has already started. PayloadId: {payloadId}");

        return payloadId;
    }

    private Block ProduceEmptyBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Preparing empty block from payload {payloadId} with parent {parentHeader}");
        Block emptyBlock = _blockProducer.PrepareEmptyBlock(parentHeader, payloadAttributes, token);
        if (_logger.IsTrace) _logger.Trace($"Prepared empty block from payload {payloadId} block: {emptyBlock}");
        return emptyBlock;
    }

    protected virtual void ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, CancellationToken token) =>
        _payloadStorage.AddOrUpdate(payloadId,
            id => CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentBlockFees, token),
            (id, currentContext) =>
            {
                // if there is payload improvement and its not yet finished leave it be
                if (!currentContext.ImprovementTask.IsCompleted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, previous improvement hasn't finished");
                    return currentContext;
                }

                IBlockImprovementContext newContext = CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentContext.CurrentBestBlock!, startDateTime, currentContext.BlockFees, token);
                currentContext.Dispose();
                return newContext;
            });


    private IBlockImprovementContext CreateBlockImprovementContext(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Start improving block from payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");

        long startTimestamp = Stopwatch.GetTimestamp();
        IBlockImprovementContext context = _blockImprovementContextFactory.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime, currentBlockFees, token);
        context.ImprovementTask.ContinueWith((b) =>
        {
            if (b.IsCompletedSuccessfully)
            {
                Block? block = b.Result;
                if (!ReferenceEquals(block, currentBestBlock))
                {
                    LogProductionResult(b, context.BlockFees, Stopwatch.GetElapsedTime(startTimestamp));
                }
            }
        });
        context.ImprovementTask.ContinueWith(async _ =>
        {
            // Attempt to improve the block if there is still enough time left in the slot after a delay

            // Calculate the time elapsed since the last improvement attempt started
            TimeSpan elapsedTimeSinceStart = Stopwatch.GetElapsedTime(startTimestamp);
            // Estimate the duration of the next improvement attempt.
            // Assume it will take twice as long from new txs added.
            TimeSpan estimatedNextImprovementDuration = elapsedTimeSinceStart * 2;
            // Estimate when the next improvement attempt would complete if started now
            DateTimeOffset estimatedNextImprovementCompletion = DateTimeOffset.UtcNow + estimatedNextImprovementDuration;
            // Determine when we should have a good block built by (e.g., 85% of the slot duration)
            // If we are in the last 85% of the block we will stop waiting between
            // improvements as inclusion is very time sensitive and we don't know when
            // GetPayload will be called; and instead we will rely on cancellation
            // to stop improving.
            DateTimeOffset slotImprovementDeadline = startDateTime + _timePerSlot * 0.85;
            if (!token.IsCancellationRequested)
            {
                if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} will be improved in {_improvementDelay.TotalMilliseconds}ms");
                try
                {
                    // Calculate the remaining time in the slot after the estimated completion of the next improvement
                    TimeSpan remainingSlotTime = PositiveOrZero(slotImprovementDeadline - estimatedNextImprovementCompletion);
                    // Calculate the remaining delay before starting the next improvement attempt
                    TimeSpan remainingImprovementDelay = PositiveOrZero(_improvementDelay - elapsedTimeSinceStart);
                    // Adjust the wait time to be the lesser of the remaining slot time and the remaining improvement delay
                    TimeSpan adjustedWaitTime = remainingSlotTime > remainingImprovementDelay ? remainingImprovementDelay : remainingSlotTime;

                    // Wait for the adjusted time or until cancellation is requested
                    await Task.Delay(adjustedWaitTime, token);
                    if (_blockProducer.SupportsNotifications)
                    {
                        await Task.WhenAny(_newPendingTxWaiter.Task, token.AsTask());
                        // Rearm the txPool listener
                        _newPendingTxWaiter = new TaskCompletionSource();
                    }

                    // Proceed if not cancelled and the context is still valid
                    if (!token.IsCancellationRequested && !context.Disposed) // if GetPayload wasn't called for this item or it wasn't cleared
                    {
                        // Use the current best block from context or fallback to the provided one
                        Block newBestBlock = context.CurrentBestBlock ?? currentBestBlock;
                        // Attempt to improve the block
                        ImproveBlock(payloadId, parentHeader, payloadAttributes, newBestBlock, startDateTime, context.BlockFees, token);
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, it was retrieved");
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, no more time in slot");
            }
        });

        return context;
    }

    private static TimeSpan PositiveOrZero(TimeSpan t)
        => t < TimeSpan.Zero ? TimeSpan.Zero : t;

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

    private Block? LogProductionResult(Task<Block?> t, UInt256 blockFees, TimeSpan time)
    {
        const long weiToEth = 1_000_000_000_000_000_000;

        if (t.IsCompletedSuccessfully)
        {
            Block? block = t.Result;
            if (block is not null)
            {
                BlockImproved?.Invoke(this, new BlockEventArgs(block));
                if (_logger.IsInfo)
                {
                    if (block.Difficulty != 0)
                    {
                        _logger.Info($"Built block {blockFees.ToDecimal(null) / weiToEth,5:N3}Eth {block.ToString(Block.Format.HashNumberDiffAndTx)} | {time.TotalMilliseconds,7:N2} ms");
                    }
                    else
                    {
                        _logger.Info($"Built block {blockFees.ToDecimal(null) / weiToEth,5:N3}Eth {block.ToString(Block.Format.HashNumberMGasAndTx)} | {time.TotalMilliseconds,7:N2} ms");
                    }
                }
            }
            else
            {
                if (_logger.IsInfo) _logger.Info("Failed to improve post-merge block");
            }
        }
        else if (t.IsFaulted)
        {
            if (_logger.IsError) _logger.Error("Post merge block improvement failed", t.Exception);
        }
        else if (t.IsCanceled)
        {
            if (_logger.IsInfo) _logger.Info($"Post-merge block improvement was canceled");
        }

        return t.Result;
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
                    await Task.WhenAny(blockContext.ImprovementTask, Task.Delay(GetPayloadWaitForFullBlockMillisecondsDelay, _tokenSource.Token));
                }
                CancelOngoingImprovements();

                return blockContext;
            }
        }
        CancelOngoingImprovements();

        return null;
    }

    public CancellationTokenSource CancelOngoingImprovements()
    {
        CancellationTokenSource tokenSource = _tokenSource;
        CancellationTokenSource newTokenSource = new();
        _tokenSource = newTokenSource;
        CancellationTokenExtensions.CancelDisposeAndClear(ref tokenSource!);
        return newTokenSource;
    }

    public void Dispose()
    {
        _isDisposed = true;
        CancellationTokenExtensions.CancelDisposeAndClear(ref _tokenSource!);
    }

    public event EventHandler<BlockEventArgs>? BlockImproved;
}
