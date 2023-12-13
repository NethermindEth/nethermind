// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="TxReceipt"/>.
/// </summary>
public class ReceiptTrie : PatriciaTrie<TxReceipt>
{
    private static readonly ReceiptMessageDecoder _decoder = new();

    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    public ReceiptTrie(IReceiptSpec spec, IEnumerable<TxReceipt> receipts, bool canBuildProof = false, ICappedArrayPool? bufferPool = null)
        : base(null, canBuildProof, bufferPool: bufferPool)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(receipts);

        if (receipts.Any())
        {
            Initialize(receipts, spec);
            UpdateRootHash();
        }
    }

    private void Initialize(IEnumerable<TxReceipt> receipts, IReceiptSpec spec)
    {
        RlpBehaviors behavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
                                | RlpBehaviors.SkipTypedWrapping;
        int key = 0;

        foreach (TxReceipt? receipt in receipts)
        {
            CappedArray<byte> buffer = _decoder.EncodeToCappedArray(receipt, behavior, bufferPool: _bufferPool);
            CappedArray<byte> keyBuffer = (key++).EncodeToCappedArray(bufferPool: _bufferPool);
            Set(keyBuffer.AsSpan(), buffer);
        }
    }

    protected override void Initialize(IEnumerable<TxReceipt> list) => throw new NotSupportedException();

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, IList<TxReceipt> txReceipts)
    {
        TrackingCappedArrayPool cappedArrayPool = new(txReceipts.Count * 4);
        Hash256 receiptsRoot = new ReceiptTrie(receiptSpec, txReceipts, bufferPool: cappedArrayPool).RootHash;
        cappedArrayPool.ReturnAll();
        return receiptsRoot;
    }
}
