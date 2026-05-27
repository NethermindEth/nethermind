// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockBody(Transaction[]? transactions, BlockHeader[]? uncles, Withdrawal[]? withdrawals = null) : IEquatable<BlockBody>
    {
        public BlockBody() : this(null, null, null) { }

        public BlockBody WithChangedTransactions(Transaction[] transactions) => new(transactions, Uncles, Withdrawals);

        public BlockBody WithChangedUncles(BlockHeader[] uncles) => new(Transactions, uncles, Withdrawals);

        public BlockBody WithChangedWithdrawals(Withdrawal[]? withdrawals) => new(Transactions, Uncles, withdrawals);

        public static BlockBody WithOneTransactionOnly(Transaction tx) => new([tx], null);

        public Transaction[] Transactions { get; internal set; } = transactions ?? [];

        public BlockHeader[] Uncles { get; } = uncles ?? [];

        public Withdrawal[]? Withdrawals { get; } = withdrawals;

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0 && (Withdrawals?.Length ?? 0) == 0;

        public bool Equals(BlockBody? other) =>
            ReferenceEquals(this, other) ||
            other is not null &&
            Transactions.AsSpan().SequenceEqual(other.Transactions) &&
            Uncles.AsSpan().SequenceEqual(other.Uncles) &&
            (Withdrawals is null) == (other.Withdrawals is null) &&
            (Withdrawals is null || Withdrawals.AsSpan().SequenceEqual(other.Withdrawals));

        public override bool Equals(object? obj) => obj is BlockBody other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hashCode = new();
            AddSequenceHashCode(ref hashCode, Transactions);
            AddSequenceHashCode(ref hashCode, Uncles);
            AddSequenceHashCode(ref hashCode, Withdrawals);
            return hashCode.ToHashCode();
        }

        private static void AddSequenceHashCode<T>(ref HashCode hashCode, T[]? values)
        {
            if (values is null)
            {
                hashCode.Add(0);
                return;
            }

            hashCode.Add(values.Length);
            foreach (T value in values)
            {
                hashCode.Add(value);
            }
        }
    }
}
