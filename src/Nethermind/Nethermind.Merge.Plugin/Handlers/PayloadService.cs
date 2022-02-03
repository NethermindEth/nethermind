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
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// A cache of pending payloads. A payload is created whenever a consensus client requests a payload creation.
    /// Each payload is assigned a payload ID which can be used by the consensus client to retrieve payload later
    /// by calling a GetPayload method.
    /// https://hackmd.io/@n0ble/kintsugi-spec
    /// </summary>
    public class PayloadService : IPayloadService
    {
        private readonly Eth2BlockProductionContext _idealBlockContext;
        private readonly IInitConfig _initConfig;
        private readonly ISealer _sealer;
        private readonly ILogger _logger;
        private ulong _secondsPerSlot = 12; // in seconds

        private readonly TimeSpan _timeout ;

        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<string, IdealBlockContext> _payloadStorage = new();
        private TaskQueue _taskQueue = new();

        public PayloadService(
            Eth2BlockProductionContext idealBlockContext,
            IInitConfig initConfig,
            ISealer sealer,
            IMergeConfig mergeConfig,
            ILogManager logManager)
        {
            _idealBlockContext = idealBlockContext;
            _initConfig = initConfig;
            _sealer = sealer;
            _secondsPerSlot = mergeConfig.SecondsPerSlot;
            _timeout = TimeSpan.FromSeconds(_secondsPerSlot);
            
            _logger = logManager.GetClassLogger();
        }

        public async Task<byte[]> StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            byte[] payloadId = ComputeNextPayloadId(parentHeader, payloadAttributes);
            payloadAttributes.SuggestedFeeRecipient = _sealer.Address != Address.Zero ? _sealer.Address : payloadAttributes.SuggestedFeeRecipient;
            using CancellationTokenSource cts = new(_timeout);
            var blockProductionTask = PreparePayload(payloadId, parentHeader, payloadAttributes);
            _taskQueue.Enqueue(() => blockProductionTask);
            return payloadId;
        }

        private Task PreparePayload(byte[] payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            Block emptyBlock = ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes);
            return GeneratePayload(payloadId, parentHeader, payloadAttributes, emptyBlock);
        }
        
        private Task GeneratePayload(byte[] payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block emptyBlock)
        {
            using CancellationTokenSource cts = new(_timeout);
            
            return ProduceIdealBlock(payloadId, parentHeader, payloadAttributes, emptyBlock, cts);
            
            // await Task.Delay(TimeSpan.FromSeconds(_cleanupDelay), CancellationToken.None)
            //     .ContinueWith(_ =>
            //     {
            //         if (_logger.IsDebug) _logger.Debug($"Cleaning up payload {payloadId}");
            //     });
            // await CleanupOldPayloadWithDelay(payloadId, TimeSpan.FromSeconds(_cleanupDelay));
        }

        private Block ProduceEmptyBlock(byte[] payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            if (_logger.IsTrace) _logger.Trace($"Preparing empty block from payload {payloadId} with parent {parentHeader}");
            Block emptyBlock = _idealBlockContext.BlockProducer.PrepareEmptyBlock(parentHeader, payloadAttributes);
            if (_logger.IsTrace) _logger.Trace($"Prepared empty block from payload {payloadId} block: {emptyBlock}");
            return emptyBlock;
        }
        
        private Task ProduceIdealBlock(byte[] payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block emptyBlock, CancellationTokenSource cts)
        {
            if (_logger.IsTrace) _logger.Trace($"Preparing ideal block from payload {payloadId} with parent {parentHeader}");
            Task<Block?> idealBlockTask =
                _idealBlockContext.BlockProductionTrigger.BuildBlock(parentHeader, cts.Token, null, payloadAttributes)
                    // ToDo investigate why it is needed, because we should have processing blocks in BlockProducerBase
                    .ContinueWith((x) => Process(x.Result, parentHeader, _idealBlockContext.BlockProducerEnv),
                        cts.Token)
                    .ContinueWith(LogProductionResult, cts.Token);

            _payloadStorage[payloadId.ToHexString()] = new IdealBlockContext(emptyBlock, idealBlockTask, cts);
            if (_logger.IsTrace) _logger.Trace($"Prepared ideal block from payload {payloadId} block result: {idealBlockTask.Result}");
            return idealBlockTask;
        }
        
        private Block? LogProductionResult(Task<Block?> t)
        {
            if (t.IsCompletedSuccessfully)
            {
                if (t.Result != null)
                {
                    if (_logger.IsInfo)
                        _logger.Info(
                            $"Sealed eth2 block {t.Result.ToString(Block.Format.HashNumberDiffAndTx)}");
                }
                else
                {
                    if (_logger.IsInfo)
                        _logger.Info(
                            $"Failed to seal eth2 block (null seal)");
                }
            }
            else if (t.IsFaulted)
            {
                if (_logger.IsError) _logger.Error("Producing block failed", t.Exception);
            }
            else if (t.IsCanceled)
            {
                if (_logger.IsInfo) _logger.Info($"Block producing was canceled");
            }

            return t.Result;
        }

        public Block? GetPayload(byte[] payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId.ToHexString()))
            {
                _payloadStorage.TryRemove(payloadId.ToHexString(), out IdealBlockContext? blockContext);
                StopBlockProduction(blockContext);
                return blockContext.CurrentBestBlock;
            }

            return null;
        }

        private byte[] ComputeNextPayloadId(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            byte[] input = new byte[32 + 32 + 32 + 20];
            Span<byte> inputSpan = input.AsSpan();
            parentHeader.Hash!.Bytes.CopyTo(inputSpan.Slice(0, 32));
            payloadAttributes.Timestamp.ToBigEndian(inputSpan.Slice(32, 32));
            payloadAttributes.Random.Bytes.CopyTo(inputSpan.Slice(64, 32));
            payloadAttributes.SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(96, 20));
            Keccak inputHash = Keccak.Compute(input);
            return inputHash.Bytes.Slice(0, 8);
        }

        private async Task CleanupOldPayloadWithDelay(byte[] payloadId, Keccak blockHash, TimeSpan delay)
        {
            await Task.Delay(delay, CancellationToken.None);
            CleanupOldPayload(payloadId, blockHash);
        }

        private void CleanupOldPayload(byte[] payloadId, Keccak blockHash)
        {
            if (_payloadStorage.ContainsKey(payloadId.ToHexString()))
            {
                _payloadStorage.Remove(payloadId.ToHexString(), out _);
                if (_logger.IsInfo) _logger.Info($"Cleaned up payload with id={payloadId.ToHexString()} as it was not requested");
            }
        }

        private Block? Process(Block? block, BlockHeader parent, BlockProducerEnv blockProducerEnv)
        {
            if (block == null)
                return null;
            var stateProvider = blockProducerEnv.ReadOnlyStateProvider;
            var processor = blockProducerEnv.ChainProcessor;
            Block? processedBlock = null;
            block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;

            Keccak currentStateRoot = stateProvider.ResetStateTo(parent.StateRoot!);
            try
            {
                processedBlock = processor.Process(block, GetProcessingOptions(), NullBlockTracer.Instance);
            }
            finally
            {
                stateProvider.ResetStateTo(currentStateRoot);
            }

            return processedBlock;
        }

        private ProcessingOptions GetProcessingOptions()
        {
            ProcessingOptions options = ProcessingOptions.EthereumMerge | ProcessingOptions.NoValidation;

            return options;
        }

        private void StopBlockProduction(IdealBlockContext? idealBlockContext)
        {
            if (idealBlockContext is not null && !idealBlockContext.Task.IsCompleted)
            {
//                idealBlockContext.CancellationTokenSource.Cancel();
            }
        }

        protected class IdealBlockContext : IDisposable
        {
            public IdealBlockContext(Block currentBestBlock, Task<Block?> task, CancellationTokenSource cancellationTokenSource)
            {
                Task = task.ContinueWith(SetCurrentBestBlock, cancellationTokenSource.Token);
                CancellationTokenSource = cancellationTokenSource;
                CurrentBestBlock = currentBestBlock;
            }

            public Block CurrentBestBlock { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; }
            public Task<Block?> Task { get; }

            public void Dispose()
            {
                CancellationTokenSource.Dispose();
            }
            
            private Block? SetCurrentBestBlock(Task<Block?> t)
            {
                if (t.IsCompletedSuccessfully)
                {
                    if (t.Result != null)
                    {
                        CurrentBestBlock = t.Result;
                    }
                }
                return t.Result;
            }
        }
    }
}
