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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using Nethermind.Int256;

namespace Nethermind.Core.Eip2930
{
    public class AccessList
    {
        public AccessList(IReadOnlyDictionary<Address, IReadOnlySet<UInt256>> data,
            Queue<object>? orderQueue = null)
        {
            Data = data;
            OrderQueue = orderQueue;
        }

        public IReadOnlyDictionary<Address, IReadOnlySet<UInt256>> Data { get; }

        /// <summary>
        /// Only used for access lists generated outside of Nethermind
        /// </summary>
        public IReadOnlyCollection<object>? OrderQueue { get; }
        
        /// <summary>
        /// Has no duplicate entries (allows for more efficient serialization / deserialization)
        /// </summary>
        public bool IsNormalized => OrderQueue is null;
    }
}
