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
public class ReceiptTrie<TReceipt> : PatriciaTrie<TReceipt>
{
    private readonly IRlpStreamDecoder<TReceipt> _decoder;
    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    public ReceiptTrie(IReceiptSpec spec, ReadOnlySpan<TReceipt> receipts, IRlpStreamDecoder<TReceipt> trieDecoder, bool canBuildProof = false, ICappedArrayPool? bufferPool = null)
        : base(null, canBuildProof, bufferPool: bufferPool)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(trieDecoder);
        _decoder = trieDecoder;

        if (receipts.Length > 0)
        {
            Initialize(receipts, spec);
            UpdateRootHash();
        }
    }

    private void Initialize(ReadOnlySpan<TReceipt> receipts, IReceiptSpec spec)
    {
        RlpBehaviors behavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        int key = 0;

        foreach (TReceipt? receipt in receipts)
        {
            SpanSource buffer = _decoder.EncodeToSpanSource(receipt, rlpBehaviors: behavior, bufferPool: _bufferPool);
            SpanSource keyBuffer = key.EncodeToSpanSource(_bufferPool);
            key++;

            Set(keyBuffer.Span, buffer);
        }
    }

    protected override void Initialize(ReadOnlySpan<TReceipt> list) => throw new NotSupportedException();

    public static byte[][] CalculateReceiptProofs(IReleaseSpec spec, ReadOnlySpan<TReceipt> receipts, int index, IRlpStreamDecoder<TReceipt> decoder)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(receipts.Length * 4);
        return new ReceiptTrie<TReceipt>(spec, receipts, decoder, canBuildProof: true, cappedArrayPool).BuildProof(index);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, ReadOnlySpan<TReceipt> txReceipts, IRlpStreamDecoder<TReceipt> decoder)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(txReceipts.Length * 4);
        Hash256 receiptsRoot = new ReceiptTrie<TReceipt>(receiptSpec, txReceipts, decoder, bufferPool: cappedArrayPool).RootHash;
        return receiptsRoot;
    }
}
