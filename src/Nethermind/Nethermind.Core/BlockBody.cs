//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;

namespace Nethermind.Core
{
    public class BlockBody
    {
        // TODO Remove withdrawals default value
        public BlockBody(Transaction[]? transactions, BlockHeader[]? uncles, Withdrawal[]? withdrawals = null)
        {
            Transactions = transactions ?? Array.Empty<Transaction>();
            Uncles = uncles ?? Array.Empty<BlockHeader>();
            Withdrawals = withdrawals ?? Array.Empty<Withdrawal>();
        }

        public BlockBody() : this(null, null, null) { }

        public BlockBody WithChangedTransactions(Transaction[] transactions) => new(transactions, Uncles, Withdrawals);

        public BlockBody WithChangedUncles(BlockHeader[] uncles) => new(Transactions, uncles, Withdrawals);

        public BlockBody WithChangedWithdrawals(Withdrawal[] withdrawals) => new(Transactions, Uncles, withdrawals);

        public static BlockBody WithOneTransactionOnly(Transaction tx) => new(new[] { tx }, null, null);

        public Transaction[] Transactions { get; internal set; }

        public BlockHeader[] Uncles { get; }

        public Withdrawal[] Withdrawals { get; internal set; }

        public static readonly BlockBody Empty = new();

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0 && Withdrawals.Length == 0;
    }
}
