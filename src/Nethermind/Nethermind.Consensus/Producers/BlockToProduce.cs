// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;

//TODO: Redo clique block producer
[assembly: InternalsVisibleTo("Nethermind.Consensus.Clique")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Consensus.Producers
{
    internal class BlockToProduce : Block
    {
        // Changes visibility of Transactions property setter to public
        public new Transaction[] Transactions
        {
            get => base.Transactions;
            set => base.Transactions = value;
        }

        public BlockToProduce(
            BlockHeader blockHeader,
            IEnumerable<Transaction> transactions,
            IEnumerable<BlockHeader> uncles,
            IEnumerable<Withdrawal>? withdrawals = null)
            : base(blockHeader, Array.Empty<Transaction>(), uncles, withdrawals)
        {
            Transactions = transactions is Transaction[] transactionsArray
                ? transactionsArray
                : transactions.ToArray();
        }
    }
}
