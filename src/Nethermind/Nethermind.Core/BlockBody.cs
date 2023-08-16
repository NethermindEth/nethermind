// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Verkle;

namespace Nethermind.Core
{
    public class BlockBody
    {
        public BlockBody(Transaction[]? transactions, BlockHeader[]? uncles, Withdrawal[]? withdrawals = null, ExecutionWitness? execWitness = null)
        {
            Transactions = transactions ?? Array.Empty<Transaction>();
            Uncles = uncles ?? Array.Empty<BlockHeader>();
            Withdrawals = withdrawals;
            ExecutionWitness = execWitness;
        }

        public BlockBody() : this(null, null, null) { }

        public BlockBody WithChangedTransactions(Transaction[] transactions) => new(transactions, Uncles, Withdrawals);

        public BlockBody WithChangedUncles(BlockHeader[] uncles) => new(Transactions, uncles, Withdrawals);

        public BlockBody WithChangedWithdrawals(Withdrawal[]? withdrawals) => new(Transactions, Uncles, withdrawals);
        public BlockBody WithChangedExecutionWitness(ExecutionWitness? witness) => new(Transactions, Uncles, Withdrawals, witness);

        public static BlockBody WithOneTransactionOnly(Transaction tx) => new(new[] { tx }, null, null);

        public Transaction[] Transactions { get; internal set; }

        public BlockHeader[] Uncles { get; }

        public Withdrawal[]? Withdrawals { get; }

        public ExecutionWitness? ExecutionWitness { get; set; }

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0 && (Withdrawals?.Length ?? 0) == 0;
    }
}
