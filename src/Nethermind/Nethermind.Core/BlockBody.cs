//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Threading;

namespace Nethermind.Core
{
    public class BlockBody
    {
        public static int Number = 0;
        
        public BlockBody()
        {
            // Interlocked.Increment(ref Number);
            Transactions = Array.Empty<Transaction>();
            Ommers = Array.Empty<BlockHeader>();
        }
        
        public BlockBody(Transaction[] transactions, BlockHeader[] ommers)
        {
            // Interlocked.Increment(ref Number);
            Transactions = transactions;
            Ommers = ommers;
        }

        public BlockBody WithChangedTransactions(Transaction[] transactions)
        {
            return new BlockBody(transactions, Ommers);
        }
        
        public BlockBody WithChangedOmmers(BlockHeader[] ommers)
        {
            return new BlockBody(Transactions, ommers);
        }

        public Transaction[] Transactions { get; }
        public BlockHeader[] Ommers { get; }
        
        public static BlockBody Empty = new BlockBody();

        // ~BlockBody()
        // {
        //     Interlocked.Add(ref Number, -1);
        // }
    }
}