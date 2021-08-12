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

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct TxExecutionContext
    {
        public BlockHeader Header { get; }
        public Address Origin { get; }
        public UInt256 GasPrice { get; }
        
        public TxExecutionContext(BlockHeader blockHeader, Address origin, UInt256 gasPrice)
        {
            Header = blockHeader;
            Origin = origin;
            GasPrice = gasPrice;
        }
    }
}
