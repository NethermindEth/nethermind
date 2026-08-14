// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Comparer to check if two pending <see cref="Transaction"/>s compete with each other.
    /// <see cref="Transaction"/>s compete with each other if they have same <see cref="Transaction.SenderAddress"/> and <see cref="Transaction.Nonce"/>. In that case only one transaction can go into chain.
    /// </summary>
    /// <remarks>
    /// <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> replaces the account nonce with a set of
    /// keyed sequences, so a keyed transaction's slot is <c>(sender, nonce_keys, nonce_seq)</c>.
    /// </remarks>
    public class CompetingTransactionEqualityComparer : IEqualityComparer<Transaction?>
    {
        public static readonly CompetingTransactionEqualityComparer Instance = new();

        private CompetingTransactionEqualityComparer() { }

        public bool Equals(Transaction? newTx, Transaction? oldTx)
        {
            if (ReferenceEquals(newTx, oldTx)) return true;
            if (newTx is null || oldTx is null) return false;
            if (newTx.SenderAddress != oldTx.SenderAddress || newTx.Nonce != oldTx.Nonce) return false;

            return SameNonceDomain(KeyedDomain(newTx), KeyedDomain(oldTx));
        }

        public int GetHashCode(Transaction? obj)
        {
            HashCode hash = new();
            hash.Add(obj?.SenderAddress);
            hash.Add(obj?.Nonce);
            foreach (UInt256 nonceKey in obj is null ? default : KeyedDomain(obj))
            {
                hash.Add(nonceKey);
            }

            return hash.ToHashCode();
        }

        /// <summary>The keys whose sequences <paramref name="tx"/> consumes, or an empty span when it consumes the account nonce.</summary>
        /// <remarks>
        /// The set <c>[0]</c> aliases the account nonce, so it must hash and compare as the account-nonce domain:
        /// otherwise a frame transaction on key 0 stops competing with the plain transactions it does share a slot with.
        /// </remarks>
        private static ReadOnlySpan<UInt256> KeyedDomain(Transaction tx) =>
            tx.NonceKeys is { } nonceKeys && KeyedNonceManager.UsesKeyedDomain(nonceKeys) ? nonceKeys : default;

        private static bool SameNonceDomain(ReadOnlySpan<UInt256> newKeys, ReadOnlySpan<UInt256> oldKeys) =>
            newKeys.SequenceEqual(oldKeys);
    }
}
