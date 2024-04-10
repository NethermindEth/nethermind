// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.State.Proofs;

[assembly: InternalsVisibleTo("Nethermind.Consensus.Test")]

namespace Nethermind.Consensus.Processing
{
    internal static class BlockExtensions
    {
        public static Block CreateCopy(this Block block, BlockHeader header) =>
            block is BlockToProduce blockToProduce
                ? new BlockToProduce(header, blockToProduce.Transactions, blockToProduce.Uncles, blockToProduce.Withdrawals)
                : new Block(header, block.Transactions, block.Uncles, block.Withdrawals, block.Deposits, block.ValidatorExits);

        public static IEnumerable<Transaction> GetTransactions(this Block block) =>
            block is BlockToProduce blockToProduce
                ? blockToProduce.Transactions
                : block.Transactions;

        public static bool TrySetTransactions(this Block block, Transaction[] transactions)
        {
            block.Header.TxRoot = TxTrie.CalculateRoot(transactions);

            if (block is BlockToProduce blockToProduce)
            {
                blockToProduce.Transactions = transactions;
                return true;
            }

            return false;
        }

        public static bool IsByNethermindNode(this Block block) => block.Header.IsByNethermindNode();

        public static bool IsByNethermindNode(this BlockHeader block) =>
            Ascii.IsValid(block.ExtraData)
            && Encoding.ASCII.GetString(block.ExtraData ?? Array.Empty<byte>())
                .Contains(BlocksConfig.DefaultExtraData, StringComparison.InvariantCultureIgnoreCase);
    }
}
