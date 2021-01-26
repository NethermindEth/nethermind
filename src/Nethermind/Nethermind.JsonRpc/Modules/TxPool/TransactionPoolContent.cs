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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{   
    public class TxPoolContent
    {
        public TxPoolContent(TxPoolInfo info)
        {
            Pending = info.Pending.ToDictionary(k => k.Key, k => k.Value.ToDictionary(v => v.Key, v => new TransactionForRpc(null, null, null, v.Value)));
            Queued = info.Queued.ToDictionary(k => k.Key, k => k.Value.ToDictionary(v => v.Key, v => new TransactionForRpc(null, null, null, v.Value)));            
        }

        public IDictionary<Address, Dictionary<ulong, TransactionForRpc>> Pending { get; set; }
        public IDictionary<Address, Dictionary<ulong, TransactionForRpc>> Queued { get; set; }
    }
}
