// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Proofs;

public sealed partial class ReceiptTrie
{
    /// <summary>Hashes finalized receipts in transaction order, retaining only their trie leaf references.</summary>
    public sealed class StreamingRoot : IDisposable
    {
        private readonly ArrayPoolList<IndexedTrieRoot.NodeReference> _leaves;
        private readonly ReceiptEncoder _encoder;
        private int _count;
        private bool _disposed;

        public StreamingRoot(IReceiptSpec spec, int receiptCount, ReceiptMessageDecoder decoder)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(decoder);
            ArgumentOutOfRangeException.ThrowIfNegative(receiptCount);
            _leaves = new(receiptCount, receiptCount);
            _encoder = new(decoder, (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
                | RlpBehaviors.SkipTypedWrapping);
        }

        /// <summary>Appends the next receipt. Its bloom and cumulative gas must already be final.</summary>
        public void Add(TxReceipt receipt)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_count == _leaves.Count) throw new InvalidOperationException("Too many receipts for the block.");
            IndexedTrieRoot.Calculator<TxReceipt, ReceiptEncoder> calculator = new([], _encoder, _leaves.AsSpan());
            _leaves[calculator.GetPosition(_count)] = calculator.CalculateLeaf(_count, receipt);
            _count++;
        }

        /// <summary>Returns the root only after every receipt has been appended.</summary>
        public Hash256 Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_count != _leaves.Count) throw new InvalidOperationException("Incomplete receipt stream.");
            return new IndexedTrieRoot.Calculator<TxReceipt, ReceiptEncoder>([], _encoder, _leaves.AsSpan()).Calculate(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _leaves.Dispose();
        }
    }
}
