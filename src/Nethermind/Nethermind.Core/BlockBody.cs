// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Core
{
    public class BlockBody(Transaction[]? transactions, BlockHeader[]? uncles, Withdrawal[]? withdrawals = null, BlockAccessList? blockLevelAccessList = null)
    {
        public BlockBody() : this(null, null, null) { }

        public BlockBody WithChangedTransactions(Transaction[] transactions) => new(transactions, Uncles, Withdrawals);

        public BlockBody WithChangedUncles(BlockHeader[] uncles) => new(Transactions, uncles, Withdrawals);

        public BlockBody WithChangedWithdrawals(Withdrawal[]? withdrawals) => new(Transactions, Uncles, withdrawals);

        public static BlockBody WithOneTransactionOnly(Transaction tx) => new([tx], null, null);

        public Transaction[] Transactions { get; internal set; } = transactions ?? [];

        public BlockHeader[] Uncles { get; } = uncles ?? [];

        public Withdrawal[]? Withdrawals { get; } = withdrawals;
        public BlockAccessList? BlockAccessList { get; internal set; } = blockLevelAccessList;

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0 && (Withdrawals?.Length ?? 0) == 0;
    }
}
