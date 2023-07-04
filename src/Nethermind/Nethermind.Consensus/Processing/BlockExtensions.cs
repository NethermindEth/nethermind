// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Processing
{
    internal static class BlockExtensions
    {
        public static Block CreateCopy(this Block block, BlockHeader header) =>
            block is BlockToProduce blockToProduce
                ? new BlockToProduce(header, blockToProduce.Transactions, blockToProduce.Uncles, blockToProduce.Withdrawals)
                : new Block(header, block.Transactions, block.Uncles, block.Withdrawals);

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
