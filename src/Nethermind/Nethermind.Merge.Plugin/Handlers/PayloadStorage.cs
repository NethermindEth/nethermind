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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class PayloadStorage
    {
        // first BlockRequestResult is empty (without txs), second one is the ideal one
        private readonly IDictionary<ulong, Tuple<Block?, Keccak>> _payloadStorage =
            new ConcurrentDictionary<ulong, Tuple<Block?, Keccak>>();

        public async Task AddPayload(ulong payloadId, Keccak random, Block? emptyBlock, Task<Block?> blockTask)
        {
            _payloadStorage.TryAdd(payloadId, Tuple.Create(emptyBlock, random));
            Block? idealBlock = await blockTask;
            _payloadStorage[payloadId] = Tuple.Create(idealBlock, random);
        }

        public Tuple<Block?, Keccak>? GetPayload(ulong payloadId)
        {
            return _payloadStorage.ContainsKey(payloadId) ? _payloadStorage[payloadId] : null;
        }
    }
}
