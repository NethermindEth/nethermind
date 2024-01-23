// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="TxReceipt"/>.
/// </summary>
public class ReceiptTrie : PatriciaTrie<TxReceipt>
{
    private static readonly ReceiptMessageDecoder _decoder = new();

    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    public ReceiptTrie(IReceiptSpec spec, TxReceipt[] receipts, bool canBuildProof = false, ICappedArrayPool? bufferPool = null)
        : base(null, canBuildProof, bufferPool: bufferPool)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(receipts);

        if (receipts.Length > 0)
        {
            Initialize(receipts, spec);
            UpdateRootHash();
        }
    }

    private void Initialize(TxReceipt[] receipts, IReceiptSpec spec)
    {
        RlpBehaviors behavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
                                | RlpBehaviors.SkipTypedWrapping;
        int key = 0;

        foreach (TxReceipt? receipt in receipts)
        {
            CappedArray<byte> buffer = _decoder.EncodeToCappedArray(receipt, behavior, _bufferPool);
            CappedArray<byte> keyBuffer = (key++).EncodeToCappedArray(_bufferPool);

            Set(keyBuffer.AsSpan(), buffer);
        }
    }

    protected override void Initialize(TxReceipt[] list) => throw new NotSupportedException();

    public static byte[][] CalculateReceiptProofs(IReleaseSpec spec, TxReceipt[] receipts, int index)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(receipts.Length * 4);
        return new ReceiptTrie(spec, receipts, canBuildProof: true, cappedArrayPool).BuildProof(index);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, TxReceipt[] txReceipts)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(txReceipts.Length * 4);
        Hash256 receiptsRoot = new ReceiptTrie(receiptSpec, txReceipts, bufferPool: cappedArrayPool).RootHash;
        return receiptsRoot;
    }
}
