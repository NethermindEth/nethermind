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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.State;
using Result = Nethermind.Core.Result;

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
        private readonly Eth2BlockProductionContext _emptyBlockContext;
        private readonly IInitConfig _initConfig;
        private readonly ISealer _sealer;
        private readonly ILogger _logger;
        private ulong _cleanupDelay = 12; // in seconds
        private TaskQueue _taskQueue = new TaskQueue();

        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(12);

        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<byte[], BlockTaskAndRandom> _payloadStorage =
            new();

        public PayloadService(
            Eth2BlockProductionContext idealBlockContext,
            Eth2BlockProductionContext emptyBlockContext,
            IInitConfig initConfig,
            ISealer sealer,
            ILogManager logManager)
        {
            _idealBlockContext = idealBlockContext;
            _emptyBlockContext = emptyBlockContext;
            _initConfig = initConfig;
            _sealer = sealer;
            _logger = logManager.GetClassLogger();
        }

        public byte[] StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            byte[] payloadId = ComputeNextPayloadId(parentHeader, payloadAttributes);
            payloadAttributes.FeeRecipient = payloadAttributes.FeeRecipient == Address.Zero ? _sealer.Address : payloadAttributes.FeeRecipient;
            GeneratePayload(payloadId, parentHeader, payloadAttributes);
            return payloadId;
        }


        private async Task GeneratePayload(byte[] payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes)
        {
            using CancellationTokenSource cts = new(_timeout);
            
            await ProduceEmptyBlock(payloadId, parentHeader, payloadAttributes, cts);
            await ProduceIdealBlock(payloadId, parentHeader, payloadAttributes, cts);
            
            await Task.Delay(TimeSpan.FromSeconds(_cleanupDelay), CancellationToken.None)
                .ContinueWith(_ =>
                {
                    if (_logger.IsDebug) _logger.Debug($"Cleaning up payload {payloadId}");
                });
            await CleanupOldPayloadWithDelay(payloadId, TimeSpan.FromSeconds(_cleanupDelay));
        }

        private async Task ProduceEmptyBlock(byte[] payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, CancellationTokenSource cts)
        {
            if (_logger.IsTrace) _logger.Trace($"Preparing empty block from payload {payloadId} with parent {parentHeader}");
            Task<Block?> emptyBlock =
                _emptyBlockContext.BlockProductionTrigger
                    .BuildBlock(parentHeader, cts.Token, null, payloadAttributes)
                    .ContinueWith((x) =>
                    {
                        x.Result.Header.StateRoot = parentHeader.StateRoot;
                        x.Result.Header.Hash = x.Result.CalculateHash();
                        return x.Result;
                    })
                    .ContinueWith(LogProductionResult, cts.Token);
            
            BlockTaskAndRandom emptyBlockTaskTuple = new(emptyBlock, payloadAttributes.Random);
            bool _ = _payloadStorage.TryAdd(payloadId, emptyBlockTaskTuple);
            await emptyBlock;
            if (_logger.IsTrace) _logger.Trace($"Prepared empty block from payload {payloadId} block result: {emptyBlock.Result}");
        }
        
        private async Task ProduceIdealBlock(byte[] payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, CancellationTokenSource cts)
        {
            if (_logger.IsTrace) _logger.Trace($"Preparing ideal block from payload {payloadId} with parent {parentHeader}");
            Task<Block?> idealBlock =
                _idealBlockContext.BlockProductionTrigger.BuildBlock(parentHeader, cts.Token, null, payloadAttributes)
                    // ToDo investigate why it is needed, because we should have processing blocks in BlockProducerBase
                    .ContinueWith((x) => Process(x.Result, parentHeader, _idealBlockContext.BlockProducerEnv), cts.Token) 
                    .ContinueWith(LogProductionResult, cts.Token);
            
            BlockTaskAndRandom idealBlockTaskTuple = new(idealBlock, payloadAttributes.Random);
            
            _payloadStorage[payloadId] = idealBlockTaskTuple;
            await idealBlock;
            if (_logger.IsTrace) _logger.Trace($"Prepared ideal block from payload {payloadId} block result: {idealBlock.Result}");
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

        public BlockTaskAndRandom? GetPayload(byte[] payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId))
            {
                _payloadStorage.TryRemove(payloadId, out BlockTaskAndRandom? payload);
                return payload;
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
            payloadAttributes.FeeRecipient.Bytes.CopyTo(inputSpan.Slice(96, 20));
            Keccak inputHash = Keccak.Compute(input);
            return inputHash.Bytes.Slice(0, 8);
        }

        private async Task CleanupOldPayloadWithDelay(byte[] payloadId, TimeSpan delay)
        {
            await Task.Delay(delay, CancellationToken.None);
            CleanupOldPayload(payloadId);
        }

        private void CleanupOldPayload(byte[] payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId))
            {
                _payloadStorage.Remove(payloadId, out _);
                if (_logger.IsInfo) _logger.Info($"Cleaned up payload with id={payloadId} as it was not requested");
            }
        }

        private Block? Process(Block block, BlockHeader parent, BlockProducerEnv blockProducerEnv)
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
            if (_initConfig.StoreReceipts)
            {
                options |= ProcessingOptions.StoreReceipts;
            }

            return options;
        }
    }
}
