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

using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Mining;

namespace Nethermind.Blockchain
{
    public class Miner
    {
        private readonly IBlockchain _blockchain;
        private readonly ITransactionStore _transactionStore;

        public Miner(IBlockchain blockchain, ITransactionStore transactionStore)
        {
            _blockchain = blockchain;
            _transactionStore = transactionStore;
        }
        
        private readonly IEthash _ethash;

        public BigInteger MinGasPrice { get; set; } = 0;
        
        public Miner(IEthash ethash)
        {
            _ethash = ethash;
        }

        public async Task<Block> MineAsync(Block block, ulong? startNonce = null)
        {
//            Transaction[] transactions = _transactionStore.GetPending();
//            List<Transaction> selected = new List<Transaction>();
//            BigInteger gasRemaining = block.Header.GasLimit;
//            foreach (Transaction transaction in transactions)
//            {
//                if (transaction.GasPrice < MinGasPrice)
//                {
//                    continue;
//                }
//                
//                if (transaction.GasLimit > gasRemaining)
//                {
//                    break;
//                }
//
//                selected.Add(transaction);
//                gasRemaining -= transaction.GasLimit;
//                
//                // TODO: any other conditions
//            }
            
            return await Task.Factory.StartNew(() => Mine(block, startNonce));
        }

        private Block Mine(Block block, ulong? startNonce)
        {
            (Keccak MixHash, ulong Nonce) result = _ethash.Mine(block.Header, startNonce);
            block.Header.Nonce = result.Nonce;
            block.Header.MixHash = result.MixHash;
            return block;
        }
    }
}