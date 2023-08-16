// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Comparer to check if two pending <see cref="Transaction"/>s compete with each other.
    /// <see cref="Transaction"/>s compete with each other if they have same <see cref="Transaction.SenderAddress"/> and <see cref="Transaction.Nonce"/>. In that case only one transaction can go into chain.
    /// </summary>
    public class CompetingTransactionEqualityComparer : IEqualityComparer<Transaction?>
    {
        public static readonly CompetingTransactionEqualityComparer Instance = new();

        private CompetingTransactionEqualityComparer() { }

        public bool Equals(Transaction? newTx, Transaction? oldTx) =>
            ReferenceEquals(newTx, oldTx) || !ReferenceEquals(newTx, null) && !ReferenceEquals(oldTx, null) && newTx.SenderAddress == oldTx.SenderAddress && newTx.Nonce == oldTx.Nonce;

        public int GetHashCode(Transaction? obj) => HashCode.Combine(obj?.SenderAddress, obj?.Nonce);
    }
}
