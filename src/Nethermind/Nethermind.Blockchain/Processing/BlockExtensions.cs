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
using System.Linq;
using Nethermind.Blockchain.Producers;
using Nethermind.Core;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Processing
{
    internal static class BlockExtensions
    {
        public static Block CreateCopy(this Block block, BlockHeader header) =>
            block is BlockToProduce blockToProduce 
                ? new BlockToProduce(header, blockToProduce.Transactions, blockToProduce.Ommers) 
                : new Block(header, block.Transactions, block.Ommers);

        public static IEnumerable<Transaction> GetTransactions(this Block block) =>
            block is BlockToProduce blockToProduce
                ? blockToProduce.Transactions
                : block.Transactions;

        public static bool TrySetTransactions(this Block block, Transaction[] transactions)
        {
            block.Header.TxRoot = new TxTrie(transactions).RootHash;
            
            if (block is BlockToProduce blockToProduce)
            {
                blockToProduce.Transactions = transactions;
                return true;
            }

            return false;
        }
    }
}
