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
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Mining;

namespace Nethermind.Blockchain
{
    public class Miner
    {
        private readonly IBlockchainProcessor _blockchain;
        private readonly IEthash _ethash;
        private readonly ITransactionStore _transactionStore;

        public Miner(IEthash ethash, IBlockchainProcessor blockchain, ITransactionStore transactionStore)
        {
            _ethash = ethash;
            _blockchain = blockchain;
            _transactionStore = transactionStore;
        }

        public BigInteger MinGasPrice { get; set; } = 0;

        public async Task<Block> MineAsync(Block block, ulong? startNonce = null)
        {
            Transaction[] transactions = _transactionStore.GetPending();
            List<Transaction> selected = new List<Transaction>();
            BigInteger gasRemaining = block.Header.GasLimit;
            foreach (Transaction transaction in transactions)
            {
                if (transaction.GasPrice < MinGasPrice)
                {
                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    break;
                }

                selected.Add(transaction);
                gasRemaining -= transaction.GasLimit;
            }

            block.Transactions = selected;

//            block.Header.Beneficiary = _beneficiary;
//            block.Header.ExtraData = Encoding.ASCII.GetBytes("Nethermind");

            Block processed = _blockchain.Try(block);

            Debug.Assert(processed.Header.TransactionsRoot != null, "transactions root");
            Debug.Assert(processed.Header.StateRoot != null, "state root");
            Debug.Assert(processed.Header.ReceiptsRoot != null, "receipts root");
            Debug.Assert(processed.Header.OmmersHash != null, "ommers hash");
            Debug.Assert(processed.Header.Bloom != null, "bloom");
            Debug.Assert(processed.Header.ExtraData != null, "extra data");

            Block minedBlock = await Task.Factory.StartNew(() => Mine(block, startNonce));
            minedBlock.Header.RecomputeHash();
            return minedBlock;
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