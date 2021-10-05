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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Timer = System.Timers.Timer;

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
        private readonly object _locker = new();
        private uint _currentPayloadId;
        private ulong _cleanupDelay = 12; // in seconds
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(12);
        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<ulong, BlockTaskAndRandom> _payloadStorage =
            new();

        public PayloadStorage(IManualBlockProductionTrigger blockProductionTrigger,
            IManualBlockProductionTrigger emptyBlockProductionTrigger)
        {
            _blockProductionTrigger = blockProductionTrigger;
            _emptyBlockProductionTrigger = emptyBlockProductionTrigger;
        }

        public async Task GeneratePayload(ulong payloadId, Keccak random, BlockHeader parentHeader, Address blockAuthor, UInt256 timestamp)
        {
            using CancellationTokenSource cts = new(_timeout);

            Task<Block?> emptyBlock = _emptyBlockProductionTrigger.BuildBlock(parentHeader, cts.Token, null, blockAuthor, timestamp);
            Task<Block?> idealBlock = _blockProductionTrigger.BuildBlock(parentHeader, cts.Token, null, blockAuthor, timestamp);
            
            BlockTaskAndRandom emptyBlockTaskTuple = new(emptyBlock, random);
            bool _ = _payloadStorage.TryAdd(payloadId, emptyBlockTaskTuple);

            BlockTaskAndRandom idealBlockTaskTuple = new(idealBlock, random);
            bool __ = _payloadStorage.TryUpdate(payloadId, idealBlockTaskTuple, emptyBlockTaskTuple);
            
            // remove after 12 seconds, it will not be needed
            await Task.Delay(TimeSpan.FromSeconds(12), cts.Token);
            CleanupOldPayload(payloadId);
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

        public void CleanupOldPayload(ulong payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId))
            {
                _payloadStorage.Remove(payloadId, out _);
            }
        }
    }
}
