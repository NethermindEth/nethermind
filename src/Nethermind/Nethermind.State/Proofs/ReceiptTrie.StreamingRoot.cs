// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Proofs;

public sealed partial class ReceiptTrie
{
    /// <summary>Hashes receipt leaves as receipts arrive in transaction order, then assembles the root.</summary>
    /// <remarks>Single-consumer only. The receipt count and each appended receipt's consensus fields must remain fixed.</remarks>
    public sealed class StreamingRoot
    {
        private readonly TxReceipt[] _receipts;
        private readonly IndexedTrieRoot.NodeReference[] _leaves;
        private readonly ReceiptEncoder _encoder;
        private int _count;

        /// <summary>Creates a root builder for a known number of receipts.</summary>
        public StreamingRoot(int count, IReceiptSpec spec, ReceiptMessageDecoder decoder)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _receipts = new TxReceipt[count];
            _leaves = new IndexedTrieRoot.NodeReference[count];
            _encoder = new(decoder, (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping);
        }

        /// <summary>Appends the next receipt and hashes its trie leaf.</summary>
        public void Append(TxReceipt receipt)
        {
            if (_count == _receipts.Length) throw new InvalidOperationException("Too many receipts.");
            int index = _count++;
            _receipts[index] = receipt;
            int zeroPosition = Math.Min(_receipts.Length - 1, 127);
            int position = index == 0 ? zeroPosition : index <= zeroPosition ? index - 1 : index;
            _leaves[position] = new IndexedTrieRoot.Calculator<TxReceipt, ReceiptEncoder>(_receipts, _encoder).CalculateLeaf(position);
        }

        /// <summary>Returns the root after every receipt has been appended.</summary>
        public Hash256 GetRoot()
        {
            if (_count != _receipts.Length) throw new InvalidOperationException("Not all receipts have arrived.");
            return new IndexedTrieRoot.Calculator<TxReceipt, ReceiptEncoder>(_receipts, _encoder, _leaves).Calculate(canBeParallel: false);
        }
    }
}
