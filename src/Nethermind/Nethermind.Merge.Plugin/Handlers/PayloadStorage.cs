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
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// A cache of pending payloads. A payload is created whenever a consensus client requests a payload creation.
    /// Each payload is assigned a payload ID which can be used by the consensus client to retrieve payload later
    /// by calling a GetPayload method.
    /// https://hackmd.io/@n0ble/consensus_api_design_space 
    /// </summary>
    public class PayloadStorage
    {
        private readonly IManualBlockProductionTrigger _blockProductionTrigger;
        private readonly IManualBlockProductionTrigger _emptyBlockProductionTrigger;
        private readonly IStateProvider _stateProvider;
        private readonly IBlockchainProcessor _processor;
        private readonly IInitConfig _initConfig;
        private readonly ILogger _logger;
        private readonly object _locker = new();
        private uint _currentPayloadId;
        private ulong _cleanupDelay = 12; // in seconds

        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(12);

        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<ulong, BlockTaskAndRandom> _payloadStorage =
            new();

        public PayloadStorage(
            IManualBlockProductionTrigger blockProductionTrigger,
            IManualBlockProductionTrigger emptyBlockProductionTrigger,
            IStateProvider stateProvider,
            IBlockchainProcessor processor,
            IInitConfig initConfig,
            ILogManager logManager)
        {
            _blockProductionTrigger = blockProductionTrigger;
            _emptyBlockProductionTrigger = emptyBlockProductionTrigger;
            _stateProvider = stateProvider;
            _processor = processor;
            _initConfig = initConfig;
            _logger = logManager.GetClassLogger();
        }


        public async Task GeneratePayload(ulong payloadId, Keccak random, BlockHeader parentHeader, Address blockAuthor,
            UInt256 timestamp)
        {
            using CancellationTokenSource cts = new(_timeout);

            Task<Block?> emptyBlock =
                _emptyBlockProductionTrigger.BuildBlock(parentHeader, cts.Token, null, blockAuthor, timestamp)
                    .ContinueWith((x) =>
                    {
                        x.Result.Header.StateRoot = parentHeader.StateRoot;
                        x.Result.Header.Hash = x.Result.CalculateHash();
                        return x.Result;
                    }); // commit when mergemock will be fixed
            //  .ContinueWith(LogProductionResult, cts.Token);
            //   .ContinueWith((x) => Process(x.Result, parentHeader), cts.Token); // commit when mergemock will be fixed
            Task<Block?> idealBlock =
                _blockProductionTrigger.BuildBlock(parentHeader, cts.Token, null, blockAuthor, timestamp)
               //     .ContinueWith(LogProductionResult, cts.Token);
                    .ContinueWith((x) => Process(x.Result, parentHeader), cts.Token); // commit when mergemock will be fixed

            BlockTaskAndRandom emptyBlockTaskTuple = new(emptyBlock, random);
            bool _ = _payloadStorage.TryAdd(payloadId, emptyBlockTaskTuple);

            BlockTaskAndRandom idealBlockTaskTuple = new(idealBlock, random);
            await idealBlock;
            bool __ = _payloadStorage.TryUpdate(payloadId, idealBlockTaskTuple, emptyBlockTaskTuple);

            // remove after 12 seconds, it will not be needed
            await CleanupOldPayloadWithDelay(payloadId, TimeSpan.FromSeconds(_cleanupDelay));
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

        public BlockTaskAndRandom? GetPayload(ulong payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId))
            {
                _payloadStorage.TryRemove(payloadId, out BlockTaskAndRandom? payload);
                return payload;
            }

            return null;
        }

        public uint RentNextPayloadId()
        {
            lock (_locker)
            {
                while (_payloadStorage.ContainsKey(_currentPayloadId))
                {
                    if (_currentPayloadId == uint.MaxValue)
                        _currentPayloadId = 0;
                    else
                        ++_currentPayloadId;
                }

                uint rentedPayloadId = _currentPayloadId;
                ++_currentPayloadId;
                return rentedPayloadId;
            }
        }

        private async Task CleanupOldPayloadWithDelay(ulong payloadId, TimeSpan delay)
        {
            await Task.Delay(delay, CancellationToken.None);
            CleanupOldPayload(payloadId);
        }

        private void CleanupOldPayload(ulong payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId))
            {
                _payloadStorage.Remove(payloadId, out _);
                if (_logger.IsInfo) _logger.Info($"Cleaned up payload with id={payloadId} as it was not requested");
            }
        }

        private Block? Process(Block block, BlockHeader parent)
        {
            if (block == null)
                return null;
            Block? processedBlock = null;
            block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;

            Keccak currentStateRoot = _stateProvider.ResetStateTo(parent.StateRoot!);
            try
            {
                processedBlock = _processor.Process(block, GetProcessingOptions(), NullBlockTracer.Instance);
            }
            finally
            {
                _stateProvider.ResetStateTo(currentStateRoot);
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
