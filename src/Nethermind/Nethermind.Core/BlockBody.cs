// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public class BlockBody(Transaction[]? transactions, BlockHeader[]? uncles, Withdrawal[]? withdrawals = null, byte[]? starkProof = null)
    {
        public BlockBody() : this(null, null, null) { }

        public BlockBody WithChangedTransactions(Transaction[] transactions) => new(transactions, Uncles, Withdrawals, StarkProof);

        public BlockBody WithChangedUncles(BlockHeader[] uncles) => new(Transactions, uncles, Withdrawals, StarkProof);

        public BlockBody WithChangedWithdrawals(Withdrawal[]? withdrawals) => new(Transactions, Uncles, withdrawals, StarkProof);

        public static BlockBody WithOneTransactionOnly(Transaction tx) => new([tx], null);

        public Transaction[] Transactions { get; internal set; } = transactions ?? [];

        public BlockHeader[] Uncles { get; } = uncles ?? [];

        public Withdrawal[]? Withdrawals { get; } = withdrawals;

        /// <summary>The EIP-8288 proof bytes; null before activation, empty for an empty dependency set.</summary>
        public byte[]? StarkProof { get; set; } = starkProof;

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0 && (Withdrawals?.Length ?? 0) == 0 && StarkProof is null;
    }
}
