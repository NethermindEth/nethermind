﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Handlers.V1;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    /// <summary>
    /// A cache of pending payloads. A payload is created whenever a consensus client requests a payload creation in <see cref="ForkchoiceUpdatedV1Handler"/>.
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_forkchoiceupdatedv1"/>
    /// Each payload is assigned a payloadId which can be used by the consensus client to retrieve payload later by calling a <see cref="GetPayloadV1Handler"/>.
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_getpayloadv1"/>
    /// </summary>
    public class PayloadPreparationService : IPayloadPreparationService
    {
        private readonly PostMergeBlockProducer _blockProducer;
        private readonly IBlockImprovementContextFactory _blockImprovementContextFactory;
        private readonly ILogger _logger;
        private readonly List<string> _payloadsToRemove = new();

        // by default we will cleanup the old payload once per six slot. There is no need to fire it more often
        public const int SlotsPerOldPayloadCleanup = 6;
        private readonly TimeSpan _cleanupOldPayloadDelay;

        // first ExecutionPayloadV1 is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<string, IBlockImprovementContext> _payloadStorage = new();

        public PayloadPreparationService(
            PostMergeBlockProducer blockProducer,
            IBlockImprovementContextFactory blockImprovementContextFactory,
            ITimerFactory timerFactory,
            ILogManager logManager,
            TimeSpan timePerSlot,
            int slotsPerOldPayloadCleanup = SlotsPerOldPayloadCleanup)
        {
            _blockProducer = blockProducer;
            _blockImprovementContextFactory = blockImprovementContextFactory;
            TimeSpan timeout = timePerSlot;
            _cleanupOldPayloadDelay = 2 * timePerSlot; // 2 * slots time
            ITimer timer = timerFactory.CreateTimer(slotsPerOldPayloadCleanup * timeout);
            timer.Elapsed += CleanupOldPayloads;
            timer.Start();

            _logger = logManager.GetClassLogger();
        }

        public string StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            string payloadId = ComputeNextPayloadId(parentHeader, payloadAttributes);
            if (!_payloadStorage.ContainsKey(payloadId))
            {
                Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes);
                ImproveBlock(payloadId, parentHeader, payloadAttributes, emptyBlock);
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

        private void ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block emptyBlock)
        {
            if (_logger.IsTrace) _logger.Trace($"Start improving block from payload {payloadId} with parent {parentHeader}");
            IBlockImprovementContext blockImprovementContext = _blockImprovementContextFactory.StartBlockImprovementContext(emptyBlock, parentHeader, payloadAttributes);
            blockImprovementContext.ImprovementTask.ContinueWith(LogProductionResult);
            if (!_payloadStorage.TryAdd(payloadId, blockImprovementContext))
            {
                blockImprovementContext.Dispose();
            }
        }

        private void CleanupOldPayloads(object? sender, EventArgs e)
        {
            if (_logger.IsTrace) _logger.Trace($"Started old payloads cleanup");
            UnixTime utcNow = new(DateTimeOffset.Now);

            foreach (KeyValuePair<string, IBlockImprovementContext> payload in _payloadStorage)
            {
                if (payload.Value.CurrentBestBlock is not null && payload.Value.CurrentBestBlock.Timestamp + (uint)_cleanupOldPayloadDelay.Seconds <= utcNow.Seconds)
                {
                    if (_logger.IsInfo) _logger.Info($"A new payload to remove: {payload.Key}, Current time {utcNow.Seconds}, Payload timestamp: {payload.Value.CurrentBestBlock.Timestamp}");
                    _payloadsToRemove.Add(payload.Key);
                }
            }

            foreach (string payloadToRemove in _payloadsToRemove)
            {
                if (_payloadStorage.TryRemove(payloadToRemove, out IBlockImprovementContext? context))
                {
                    context.Dispose();
                    if (_logger.IsInfo) _logger.Info($"Cleaned up payload with id={payloadToRemove} as it was not requested");
                }
            }

            _payloadsToRemove.Clear();
            if (_logger.IsTrace) _logger.Trace($"Finished old payloads cleanup");
        }

        private Block? LogProductionResult(Task<Block?> t)
        {
            if (t.IsCompletedSuccessfully)
            {
                if (t.Result != null)
                {
                    BlockImproved?.Invoke(this, new BlockEventArgs(t.Result));
                    if (_logger.IsInfo) _logger.Info($"Produced post-merge block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Failed to produce post-merge block");
                }
            }
            else if (t.IsFaulted)
            {
                if (_logger.IsError) _logger.Error("Post merge block producing failed", t.Exception);
            }
            else if (t.IsCanceled)
            {
                if (_logger.IsInfo) _logger.Info($"Post-merge block improvement was canceled");
            }

            return t.Result;
        }

        public Block? GetPayload(string payloadId)
        {
            if (_payloadStorage.TryGetValue(payloadId, out IBlockImprovementContext? blockContext))
            {
                using (blockContext)
                {
                    return blockContext.CurrentBestBlock;
                }
            }

            return null;
        }

        public event EventHandler<BlockEventArgs>? BlockImproved;

        private string ComputeNextPayloadId(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            Span<byte> inputSpan = stackalloc byte[32 + 32 + 32 + 20];
            parentHeader.Hash!.Bytes.CopyTo(inputSpan.Slice(0, 32));
            payloadAttributes.Timestamp.ToBigEndian(inputSpan.Slice(32, 32));
            payloadAttributes.PrevRandao.Bytes.CopyTo(inputSpan.Slice(64, 32));
            payloadAttributes.SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(96, 20));
            ValueKeccak inputHash = ValueKeccak.Compute(inputSpan);
            return inputHash.BytesAsSpan.Slice(0, 8).ToHexString(true);
        }
    }
}
