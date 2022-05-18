//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// A cache of pending payloads. A payload is created whenever a consensus client requests a payload creation.
    /// Each payload is assigned a payload ID which can be used by the consensus client to retrieve payload later
    /// by calling a GetPayload method.
    /// https://hackmd.io/@n0ble/kiln-spec
    /// </summary>
    public class PayloadPreparationService : IPayloadPreparationService
    {
        private readonly PostMergeBlockProducer _blockProducer;
        private readonly IManualBlockProductionTrigger _blockProductionTrigger;
        private readonly ISealer _sealer;
        private readonly ILogger _logger;
        private readonly List<string> _payloadsToRemove = new();
        
        // by default we will cleanup the old payload once per six slot. There is no need to fire it more often
        private const int SlotsPerOldPayloadCleanup = 6;
        private readonly  ulong _cleanupOldPayloadDelay;
        private readonly TimeSpan _timeout;

        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<string, BlockImprovementContext> _payloadStorage = new();
        private TaskQueue _taskQueue = new();

        public PayloadPreparationService(
            PostMergeBlockProducer blockProducer,
            IManualBlockProductionTrigger blockProductionTrigger,
            ISealer sealer,
            IMergeConfig mergeConfig,
            ITimerFactory timerFactory,
            ILogManager logManager,
            int slotsPerOldPayloadCleanup = SlotsPerOldPayloadCleanup)
        {
            _blockProducer = blockProducer;
            _blockProductionTrigger = blockProductionTrigger;
            _sealer = sealer;
            _timeout = TimeSpan.FromSeconds(mergeConfig.SecondsPerSlot);

            _cleanupOldPayloadDelay = 2 * mergeConfig.SecondsPerSlot * 1000; // 2 * slots time * 1000 (converting seconds to miliseconds)
            ITimer timer = timerFactory.CreateTimer(slotsPerOldPayloadCleanup * _timeout);
            timer.Elapsed += CleanupOldPayloads;
            timer.AutoReset = false;
            timer.Start();

            _logger = logManager.GetClassLogger();
        }

        public string? StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            string payloadId = ComputeNextPayloadId(parentHeader, payloadAttributes).ToHexString(true);
            if (!_payloadStorage.ContainsKey(payloadId))
            {
                payloadAttributes.SuggestedFeeRecipient = _sealer.Address != Address.Zero
                    ? _sealer.Address
                    : payloadAttributes.SuggestedFeeRecipient;
                Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes);
                Task blockImprovementTask = ImproveBlock(payloadId, parentHeader, payloadAttributes, emptyBlock);
                _taskQueue.Enqueue(() => blockImprovementTask);
            }
            else
                if (_logger.IsInfo) _logger.Info($"Payload with the same parameters has already started. PayloadId: {payloadId}");

            return payloadId;
        }

        private Block ProduceEmptyBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            if (_logger.IsTrace)
                _logger.Trace($"Preparing empty block from payload {payloadId} with parent {parentHeader}");
            Block emptyBlock = _blockProducer.PrepareEmptyBlock(parentHeader, payloadAttributes);
            if (_logger.IsTrace) _logger.Trace($"Prepared empty block from payload {payloadId} block: {emptyBlock}");
            return emptyBlock;
        }

        private Task ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes,
            Block emptyBlock)
        {
            if (_logger.IsTrace)
                _logger.Trace($"Start improving block from payload {payloadId} with parent {parentHeader}");
            BlockImprovementContext blockImprovementContext =
                new(emptyBlock, _blockProductionTrigger, _timeout);
            Task<Block?> idealBlockTask = blockImprovementContext.StartImprovingBlock(parentHeader, payloadAttributes)
                .ContinueWith(LogProductionResult);

            _payloadStorage[payloadId] = blockImprovementContext;
            return idealBlockTask;
        }

        private void CleanupOldPayloads(object? sender, EventArgs e)
        {
            if (_logger.IsTrace)
                _logger.Trace($"Started old payloads cleanup");
            UnixTime utcNow = new(DateTime.Now);

            foreach (KeyValuePair<string, BlockImprovementContext> payload in _payloadStorage)
            {
                if (payload.Value?.CurrentBestBlock != null &&
                    payload.Value.CurrentBestBlock.Timestamp + _cleanupOldPayloadDelay <= utcNow.Seconds)
                {
                    if (_logger.IsInfo) _logger.Info($"A new payload to remove: {payload.Key}, Current time {utcNow}, Payload timestamp: {payload.Value.CurrentBestBlock.Timestamp}");
                    _payloadsToRemove.Add(payload.Key);
                }
            }

            foreach (string payloadToRemove in _payloadsToRemove)
            {
                bool removed = _payloadStorage.TryRemove(payloadToRemove, out _);
                if (removed && _logger.IsInfo)
                    _logger.Info($"Cleaned up payload with id={payloadToRemove} as it was not requested");
            }
            
            _payloadsToRemove.Clear();
            if (_logger.IsTrace)
                _logger.Trace($"Finished old payloads cleanup");
        }

        private Block? LogProductionResult(Task<Block?> t)
        {
            if (t.IsCompletedSuccessfully)
            {
                if (t.Result != null)
                {
                    BlockImproved?.Invoke(this, new BlockEventArgs(t.Result));
                    if (_logger.IsInfo)
                        _logger.Info(
                            $"Produced post-merge block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                }
                else
                {
                    if (_logger.IsInfo)
                        _logger.Info(
                            $"Failed to produce post-merge block");
                }
            }
            else if (t.IsFaulted)
            {
                if (_logger.IsError) _logger.Error("Post merge block producing failed", t.Exception);
            }
            else if (t.IsCanceled)
            {
                if (_logger.IsInfo) _logger.Info($"Post-merge block producing was canceled");
            }

            return t.Result;
        }

        public Block? GetPayload(byte[] payloadId)
        {
            var payloadStr = payloadId.ToHexString(true);
            if (_payloadStorage.ContainsKey(payloadStr))
            {
                _payloadStorage.TryRemove(payloadStr, out BlockImprovementContext? blockContext);
                blockContext?.Cancel();
                return blockContext?.CurrentBestBlock;
            }

            return null;
        }

        public event EventHandler<BlockEventArgs>? BlockImproved;

        private byte[] ComputeNextPayloadId(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            byte[] input = new byte[32 + 32 + 32 + 20];
            Span<byte> inputSpan = input.AsSpan();
            parentHeader.Hash!.Bytes.CopyTo(inputSpan.Slice(0, 32));
            payloadAttributes.Timestamp.ToBigEndian(inputSpan.Slice(32, 32));
            payloadAttributes.PrevRandao.Bytes.CopyTo(inputSpan.Slice(64, 32));
            payloadAttributes.SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(96, 20));
            Keccak inputHash = Keccak.Compute(input);
            return inputHash.Bytes.Slice(0, 8);
        }

        class TaskQueue
        {
            private readonly SemaphoreSlim _semaphore;

            public TaskQueue()
            {
                _semaphore = new SemaphoreSlim(1);
            }

            public async Task Enqueue(Func<Task> taskGenerator)
            {
                await _semaphore.WaitAsync();
                try
                {
                    await taskGenerator();
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }
    }
}
