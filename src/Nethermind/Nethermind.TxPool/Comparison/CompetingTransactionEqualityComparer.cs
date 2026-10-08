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
    /// <remarks>EIP-8250 replaces the account nonce with keyed sequences, so a keyed transaction's slot is <c>(sender, nonce_keys, nonce_seq)</c>.</remarks>
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
            ReadOnlySpan<UInt256> keyedDomain = obj is null ? default : KeyedDomain(obj);
            // Almost every transaction consumes the account nonce; HashCode.Combine of two values runs the
            // same rounds as two Add calls, so this path is cheaper and hashes identically.
            if (keyedDomain.IsEmpty) return HashCode.Combine(obj?.SenderAddress, obj?.Nonce);

            HashCode hash = new();
            hash.Add(obj?.SenderAddress);
            hash.Add(obj?.Nonce);
            foreach (UInt256 nonceKey in keyedDomain)
            {
                hash.Add(nonceKey);
            }

            return hash.ToHashCode();
        }

        /// <summary>Whether two of a sender's transactions consume the same nonce domain, so their sequences
        /// order against each other.</summary>
        /// <remarks>Sequences in different domains advance independently, so comparing them numerically —
        /// to order, supersede or exclude — treats unrelated transactions as one another's.</remarks>
        internal static bool SameNonceDomain(Transaction newTx, Transaction oldTx) =>
            SameNonceDomain(KeyedDomain(newTx), KeyedDomain(oldTx));

        /// <summary>The keys whose sequences <paramref name="tx"/> consumes, or an empty span when it consumes the account nonce.</summary>
        /// <remarks>The set <c>[0]</c> aliases the account nonce, so it must compare as the account-nonce domain or it stops competing with plain transactions.</remarks>
        private static ReadOnlySpan<UInt256> KeyedDomain(Transaction tx) =>
            tx.NonceKeys is { } nonceKeys && KeyedNonceManager.UsesKeyedDomain(nonceKeys) ? nonceKeys : default;

        private static bool SameNonceDomain(ReadOnlySpan<UInt256> newKeys, ReadOnlySpan<UInt256> oldKeys) =>
            newKeys.SequenceEqual(oldKeys);

        /// <summary>Whether consuming one of a sender's transactions advances a sequence the other selects.</summary>
        /// <remarks>EIP-8250 consumes every key a transaction names, so two unequal key sets sharing a key
        /// invalidate one another; pending identity is the equal-set relation, being superseded is this one.</remarks>
        internal static bool OverlapsNonceDomain(Transaction newTx, Transaction oldTx) =>
            OverlapsNonceDomain(KeyedDomain(newTx), KeyedDomain(oldTx));

        private static bool OverlapsNonceDomain(ReadOnlySpan<UInt256> newKeys, ReadOnlySpan<UInt256> oldKeys)
        {
            // The account domain is one domain, shared with nothing keyed.
            if (newKeys.IsEmpty || oldKeys.IsEmpty) return newKeys.IsEmpty && oldKeys.IsEmpty;

            // Both sets are strictly increasing (EIP-8250 well-formedness), so one merge pass finds a shared key.
            int newIndex = 0;
            int oldIndex = 0;
            while (newIndex < newKeys.Length && oldIndex < oldKeys.Length)
            {
                int comparison = newKeys[newIndex].CompareTo(oldKeys[oldIndex]);
                if (comparison == 0) return true;
                if (comparison < 0) newIndex++;
                else oldIndex++;
            }

            return false;
        }
    }
}
