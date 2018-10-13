/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public static class BlockExtensions
    {
        public static Keccak CalculateTransactionsRoot(this Block block)
        {
            if (block.Transactions.Length == 0)
            {
                return PatriciaTree.EmptyTreeHash;
            }
            
            PatriciaTree txTree = new PatriciaTree();
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Rlp transactionRlp = Rlp.Encode(block.Transactions[i]);
                txTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            txTree.UpdateRootHash();
            return txTree.RootHash;
        }
    }
}