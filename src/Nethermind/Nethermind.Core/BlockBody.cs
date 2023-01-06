// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockBody
    {
        public BlockBody(Transaction[]? transactions, BlockHeader[]? uncles = null, IWithdrawal[]? withdrawals = null)
        {
            Transactions = transactions ?? Array.Empty<Transaction>();
            Uncles = uncles ?? Array.Empty<BlockHeader>();
            Withdrawals = withdrawals;
        }

        public BlockBody() : this(null) { }

        public BlockBody WithChangedTransactions(Transaction[] transactions) => new(transactions, Uncles, Withdrawals);

        public BlockBody WithChangedUncles(BlockHeader[] uncles) => new(Transactions, uncles, Withdrawals);

        public BlockBody WithChangedWithdrawals(IWithdrawal[]? withdrawals) => new(Transactions, Uncles, withdrawals);

        public static BlockBody WithOneTransactionOnly(Transaction tx) => new(new[] { tx });

        public Transaction[] Transactions { get; internal set; }

        public BlockHeader[] Uncles { get; }

        public IWithdrawal[]? Withdrawals { get; }

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0 && (Withdrawals?.Length ?? 0) == 0;
    }
}
