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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;

//TODO: Redo clique block producer
[assembly: InternalsVisibleTo("Nethermind.Consensus.Clique")]

namespace Nethermind.Blockchain.Producers
{   
    internal class BlockToProduce : Block
    {
        private IEnumerable<Transaction>? _transactions;

        public new IEnumerable<Transaction> Transactions
        {
            get => _transactions ?? base.Transactions;
            set
            {
                _transactions = value;
                if (_transactions is Transaction[] transactionsArray)
                {
                    base.Transactions = transactionsArray;
                }
            }
        }

        public BlockToProduce(BlockHeader blockHeader, IEnumerable<Transaction> transactions, IEnumerable<BlockHeader> ommers) : base(blockHeader, Array.Empty<Transaction>(), ommers)
        {
            Transactions = transactions; 
        }
    }
}
