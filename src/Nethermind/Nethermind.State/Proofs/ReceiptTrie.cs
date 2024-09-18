// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="TxReceipt"/>.
/// </summary>
public class ReceiptTrie<TReceipt> : PatriciaTrie<TReceipt>
{
    /// <inheritdoc/>
    /// <param name="spec"></param>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    /// <param name="trieDecoder"></param>
    /// <param name="bufferPool"></param>
    /// <param name="canBuildProof"></param>
    public ReceiptTrie(
        IReceiptSpec spec,
        TReceipt[] receipts,
        IRlpStreamDecoder<TReceipt> trieDecoder,
        ICappedArrayPool bufferPool,
        bool canBuildProof = false)
        : base(receipts,
            trieDecoder,
            bufferPool,
            canBuildProof,
            behaviors: (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping)
    {
    }

    public static byte[][] CalculateReceiptProofs(IReleaseSpec spec, TReceipt[] receipts, int index, IRlpStreamDecoder<TReceipt> decoder)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(receipts.Length * 4);
        return new ReceiptTrie<TReceipt>(spec, receipts, decoder, cappedArrayPool, canBuildProof: true).BuildProof(index);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, TReceipt[] txReceipts, IRlpStreamDecoder<TReceipt> decoder)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(txReceipts.Length * 4);
        Hash256 receiptsRoot = new ReceiptTrie<TReceipt>(receiptSpec, txReceipts, decoder, bufferPool: cappedArrayPool).RootHash;
        return receiptsRoot;
    }
}
