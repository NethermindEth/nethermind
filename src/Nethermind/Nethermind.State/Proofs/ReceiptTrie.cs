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
public sealed class ReceiptTrie : PatriciaTrie<TxReceipt>
{
    private readonly IRlpStreamDecoder<TxReceipt> _decoder;
    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    public ReceiptTrie(IReceiptSpec spec, ReadOnlySpan<TxReceipt> receipts, IRlpStreamDecoder<TxReceipt> trieDecoder, ICappedArrayPool bufferPool, bool canBuildProof = false)
        : this(spec, receipts, trieDecoder, bufferPool, RlpBehaviors.None, canBuildProof)
    {
    }

    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    /// <param name="additionalBehaviors">Additional RLP behaviors to apply during encoding.</param>
    public ReceiptTrie(IReceiptSpec spec, ReadOnlySpan<TxReceipt> receipts, IRlpStreamDecoder<TxReceipt> trieDecoder, ICappedArrayPool bufferPool, RlpBehaviors additionalBehaviors, bool canBuildProof = false)
        : base(null, canBuildProof, bufferPool: bufferPool)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(trieDecoder);
        _decoder = trieDecoder;

        if (receipts.Length > 0)
        {
            Initialize(receipts, spec, additionalBehaviors);
            UpdateRootHash();
        }
    }

    private void Initialize(ReadOnlySpan<TxReceipt> receipts, IReceiptSpec spec)
    {
        Initialize(receipts, spec, RlpBehaviors.None);
    }

    private void Initialize(ReadOnlySpan<TxReceipt> receipts, IReceiptSpec spec, RlpBehaviors additionalBehaviors)
    {
        RlpBehaviors behavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping | additionalBehaviors;
        int key = 0;

        foreach (TxReceipt? receipt in receipts)
        {
            SpanSource buffer = _decoder.EncodeToSpanSource(receipt, rlpBehaviors: behavior, bufferPool: _bufferPool);
            SpanSource keyBuffer = key.EncodeToSpanSource(_bufferPool);
            key++;

            Set(keyBuffer.Span, buffer);
        }
    }

    protected override void Initialize(ReadOnlySpan<TxReceipt> list) => throw new NotSupportedException();

    public static byte[][] CalculateReceiptProofs(IReleaseSpec spec, ReadOnlySpan<TxReceipt> receipts, int index, IRlpStreamDecoder<TxReceipt> decoder)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(receipts.Length * 4);
        return new ReceiptTrie(spec, receipts, decoder, cappedArrayPool, RlpBehaviors.None, canBuildProof: true).BuildProof(index);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, ReadOnlySpan<TxReceipt> txReceipts, IRlpStreamDecoder<TxReceipt> decoder)
    {
        return CalculateRoot(receiptSpec, txReceipts, decoder, RlpBehaviors.None);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, ReadOnlySpan<TxReceipt> txReceipts, IRlpStreamDecoder<TxReceipt> decoder, RlpBehaviors additionalBehaviors)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(txReceipts.Length * 4);
        Hash256 receiptsRoot = new ReceiptTrie(receiptSpec, txReceipts, decoder, cappedArrayPool, additionalBehaviors).RootHash;
        return receiptsRoot;
    }
}
