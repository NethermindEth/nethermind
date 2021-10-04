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

using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class PayloadStorage
    {
        private readonly object _locker = new();
        private uint _currentPayloadId = 0;
        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private readonly ConcurrentDictionary<ulong, BlockAndRandom> _payloadStorage =
            new();

        public async Task AddPayload(ulong payloadId, Keccak random, Block? emptyBlock, Task<Block?> blockTask)
        {
            BlockAndRandom emptyBlockTuple = new (emptyBlock, random);
            _payloadStorage.TryAdd(payloadId, emptyBlockTuple);
            Block? idealBlock = await blockTask;
            _payloadStorage.TryUpdate(payloadId, new (idealBlock, random), emptyBlockTuple);
        }

        public BlockAndRandom? GetPayload(ulong payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId))
            {
                _payloadStorage.TryRemove(payloadId, out BlockAndRandom? payload);
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
    }
}
