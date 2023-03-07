// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.BlockProduction
{
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
        public static readonly TimeSpan DefaultMinTimeForProduction = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Delay between block improvements
        /// </summary>
        private readonly TimeSpan _improvementDelay;

        /// <summary>
        /// Minimal time to try to improve block
        /// </summary>
        private readonly TimeSpan _minTimeForProduction;

        private readonly TimeSpan _cleanupOldPayloadDelay;
        private readonly TimeSpan _timePerSlot;

        // first ExecutionPayloadV1 is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<string, IBlockImprovementContext> _payloadStorage = new();
        private string _latestPayloadId = string.Empty;

        public PayloadPreparationService(
            PostMergeBlockProducer blockProducer,
            IBlockImprovementContextFactory blockImprovementContextFactory,
            ITimerFactory timerFactory,
            ILogManager logManager,
            TimeSpan timePerSlot,
            int slotsPerOldPayloadCleanup = SlotsPerOldPayloadCleanup,
            TimeSpan? improvementDelay = null,
            TimeSpan? minTimeForProduction = null)
        {
            _blockProducer = blockProducer;
            _blockImprovementContextFactory = blockImprovementContextFactory;
            _timePerSlot = timePerSlot;
            TimeSpan timeout = timePerSlot;
            _cleanupOldPayloadDelay = 3 * timePerSlot; // 3 * slots time
            _improvementDelay = improvementDelay ?? DefaultImprovementDelay;
            _minTimeForProduction = minTimeForProduction ?? DefaultMinTimeForProduction;
            ITimer timer = timerFactory.CreateTimer(slotsPerOldPayloadCleanup * timeout);
            timer.Elapsed += CleanupOldPayloads;
            timer.Start();

            _logger = logManager.GetClassLogger();
        }

        public string StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            string payloadId = ComputeNextPayloadId(parentHeader, payloadAttributes);
            _latestPayloadId = payloadId;
            if (!_payloadStorage.ContainsKey(payloadId))
            {
                Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes);
                ImproveBlock(payloadId, parentHeader, payloadAttributes, emptyBlock, DateTimeOffset.UtcNow);
            }
            else if (_logger.IsInfo) _logger.Info($"Payload with the same parameters has already started. PayloadId: {payloadId}");

            return payloadId;
        }

        private Block ProduceEmptyBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            if (_logger.IsTrace) _logger.Trace($"Preparing empty block from payload {payloadId} with parent {parentHeader}");
            Block emptyBlock = _blockProducer.PrepareEmptyBlock(parentHeader, payloadAttributes);
            if (_logger.IsTrace) _logger.Trace($"Prepared empty block from payload {payloadId} block: {emptyBlock}");
            return emptyBlock;
        }

        private void ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime) =>
            _payloadStorage.AddOrUpdate(payloadId,
                id => CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentBestBlock, startDateTime),
                (id, currentContext) =>
                {
                    // if there is payload improvement and its not yet finished leave it be
                    if (!currentContext.ImprovementTask.IsCompleted)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} won't be improved, previous improvement hasn't finished");
                        return currentContext;
                    }

                    IBlockImprovementContext newContext = CreateBlockImprovementContext(id, parentHeader, payloadAttributes, currentBestBlock, startDateTime);
                    currentContext.Dispose();
                    return newContext;
                });


        private IBlockImprovementContext CreateBlockImprovementContext(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime)
        {
            if (_logger.IsTrace) _logger.Trace($"Start improving block from payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");
            IBlockImprovementContext blockImprovementContext = _blockImprovementContextFactory.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime);
            blockImprovementContext.ImprovementTask.ContinueWith(LogProductionResult);
            blockImprovementContext.ImprovementTask.ContinueWith(async _ =>
            {
                // if after delay we still have time to try producing the block in this slot
                DateTimeOffset whenWeCouldFinishNextProduction = DateTimeOffset.UtcNow + _improvementDelay + _minTimeForProduction;
                DateTimeOffset slotFinished = startDateTime + _timePerSlot;
                if (whenWeCouldFinishNextProduction < slotFinished)
                {
                    if (_logger.IsTrace) _logger.Trace($"Block for payload {payloadId} with parent {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} will be improved in {_improvementDelay.TotalMilliseconds}ms");
                    await Task.Delay(_improvementDelay);
                    if (!blockImprovementContext.Disposed // if GetPayload wasn't called for this item or it wasn't cleared
                        && payloadId == _latestPayloadId) // we only improve on latest block payload
                    {
                        Block newBestBlock = blockImprovementContext.CurrentBestBlock ?? currentBestBlock;
                        ImproveBlock(payloadId, parentHeader, payloadAttributes, newBestBlock, startDateTime);
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

        private Block? LogProductionResult(Task<Block?> t)
        {
            if (t.IsCompletedSuccessfully)
            {
                if (t.Result is not null)
                {
                    BlockImproved?.Invoke(this, new BlockEventArgs(t.Result));
                    if (_logger.IsInfo) _logger.Info($"Improved post-merge block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
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
                    bool currentBestBlockIsEmpty = blockContext.CurrentBestBlock?.Transactions.Any() != true;
                    if (currentBestBlockIsEmpty && !blockContext.ImprovementTask.IsCompleted)
                    {
                        await Task.WhenAny(blockContext.ImprovementTask, Task.Delay(GetPayloadWaitForFullBlockMillisecondsDelay));
                    }

                    return blockContext;
                }
            }

            return null;
        }

        public event EventHandler<BlockEventArgs>? BlockImproved;

        private string ComputeNextPayloadId(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            Span<byte> inputSpan = stackalloc byte[32 + 32 + 32 + 20];
            parentHeader.Hash!.Bytes.CopyTo(inputSpan.Slice(0, 32));
            BinaryPrimitives.WriteUInt64BigEndian(inputSpan.Slice(56, 8), payloadAttributes.Timestamp);
            payloadAttributes.PrevRandao.Bytes.CopyTo(inputSpan.Slice(64, 32));
            payloadAttributes.SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(96, 20));
            ValueKeccak inputHash = ValueKeccak.Compute(inputSpan);
            return inputHash.BytesAsSpan.Slice(0, 8).ToHexString(true);
        }
    }
}
