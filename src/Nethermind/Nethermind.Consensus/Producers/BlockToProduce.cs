// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public BlockToProduce(
            BlockHeader blockHeader,
            IEnumerable<Transaction> transactions,
            IEnumerable<BlockHeader> uncles,
            IEnumerable<Withdrawal>? withdrawals = null)
            : base(blockHeader, Array.Empty<Transaction>(), uncles, withdrawals)
        {
            Transactions = transactions;
        }
    }
}
