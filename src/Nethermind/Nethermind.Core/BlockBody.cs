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

using System;

namespace Nethermind.Core
{
    public class BlockBody
    {
        public BlockBody(Transaction[]? transactions, BlockHeader[]? ommers)
        {
            Transactions = transactions ?? Array.Empty<Transaction>();
            Ommers = ommers ?? Array.Empty<BlockHeader>();
        }

        public BlockBody()
            : this(null, null)
        {
        }

        public BlockBody WithChangedTransactions(Transaction[] transactions)
        {
            return new(transactions, Ommers);
        }

        public BlockBody WithChangedOmmers(BlockHeader[] ommers)
        {
            return new(Transactions, ommers);
        }

        public static BlockBody WithOneTransactionOnly(Transaction tx)
        {
            return new(new[] {tx}, Array.Empty<BlockHeader>());
        }

        public Transaction[] Transactions { get; internal set; }
        public BlockHeader[] Ommers { get; }

        public static readonly BlockBody Empty = new();
    }
}
