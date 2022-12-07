// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockBody
    {
        public BlockBody(Transaction[]? transactions, BlockHeader[]? uncles)
        {
            Transactions = transactions ?? Array.Empty<Transaction>();
            Uncles = uncles ?? Array.Empty<BlockHeader>();
        }

        public BlockBody()
            : this(null, null)
        {
        }

        public BlockBody WithChangedTransactions(Transaction[] transactions)
        {
            return new(transactions, Uncles);
        }

        public BlockBody WithChangedUncles(BlockHeader[] uncles)
        {
            return new(Transactions, uncles);
        }

        public static BlockBody WithOneTransactionOnly(Transaction tx)
        {
            return new(new[] { tx }, Array.Empty<BlockHeader>());
        }

        public Transaction[] Transactions { get; internal set; }
        public BlockHeader[] Uncles { get; }

        public static readonly BlockBody Empty = new();

        public bool IsEmpty => Transactions.Length == 0 && Uncles.Length == 0;
    }
}
