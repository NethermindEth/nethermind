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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class PayloadStorage
    {
        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private IDictionary<UInt256, Block?> _payloadStorage =
            new ConcurrentDictionary<UInt256, Block?>();

        public async Task AddPayload(UInt256 payloadId, Block? emptyBlock, Task<Block?> blockTask)
        {
            _payloadStorage.TryAdd(payloadId, emptyBlock);
            Block? idealBlock = await blockTask;
            _payloadStorage[payloadId] = idealBlock;
        }

        public Block? GetPayload(UInt256 payloadId)
        {
            if (_payloadStorage.ContainsKey(payloadId))
            {
                Block? blockToReturn = _payloadStorage[payloadId];
                return blockToReturn;
            }
            return null;
        }
    }
}
