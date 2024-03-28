// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockBody
    {
        public BlockBody(Transaction[]? transactions, BlockHeader[]? uncles, Withdrawal[]? withdrawals = null, Deposit[]? deposits = null)
        {
            Transactions = transactions ?? Array.Empty<Transaction>();
            Uncles = uncles ?? Array.Empty<BlockHeader>();
            Withdrawals = withdrawals;
            Deposits = deposits;
        }

        public BlockBody() : this(null, null, null) { }

        public BlockBody WithChangedTransactions(Transaction[] transactions) => new(transactions, Uncles, Withdrawals, Deposits);

        public BlockBody WithChangedUncles(BlockHeader[] uncles) => new(Transactions, uncles, Withdrawals, Deposits);

        public BlockBody WithChangedWithdrawals(Withdrawal[]? withdrawals) => new(Transactions, Uncles, withdrawals, Deposits);
        public BlockBody WithChangedDeposits(Deposit[]? deposits) => new(Transactions, Uncles, Withdrawals, deposits);

        public static BlockBody WithOneTransactionOnly(Transaction tx) => new(new[] { tx }, null, null);

        public Transaction[] Transactions { get; internal set; }

        public BlockHeader[] Uncles { get; }

        public Withdrawal[]? Withdrawals { get; }

        public Deposit[]? Deposits { get; }

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0 && (Withdrawals?.Length ?? 0) == 0 && (Deposits?.Length ?? 0) == 0;
    }
}
