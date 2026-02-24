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
    public ReceiptTrie(IReceiptSpec spec, ReadOnlySpan<TxReceipt> receipts, IRlpStreamDecoder<TxReceipt> trieDecoder, ICappedArrayPool bufferPool, bool canBuildProof = false, bool canBeParallel = true)
        : base(null, canBuildProof, bufferPool: bufferPool, canBeParallel)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(trieDecoder);
        _decoder = trieDecoder;

        if (receipts.Length > 0)
        {
            Initialize(receipts, spec);
            UpdateRootHash(canBeParallel);
        }
    }

    private void Initialize(ReadOnlySpan<TxReceipt> receipts, IReceiptSpec spec)
    {
        RlpBehaviors behavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
            | RlpBehaviors.SkipTypedWrapping;
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
        bool canBeParallel = receipts.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool cappedArrayPool = new(receipts.Length * 4, canBeParallel: canBeParallel);
        return new ReceiptTrie(spec, receipts, decoder, cappedArrayPool, canBuildProof: true, canBeParallel: canBeParallel).BuildProof(index);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, ReadOnlySpan<TxReceipt> txReceipts, IRlpStreamDecoder<TxReceipt> decoder)
    {
        bool canBeParallel = txReceipts.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool cappedArrayPool = new(txReceipts.Length * 4, canBeParallel: canBeParallel);
        Hash256 receiptsRoot = new ReceiptTrie(receiptSpec, txReceipts, decoder, bufferPool: cappedArrayPool, canBeParallel: canBeParallel).RootHash;
        return receiptsRoot;
    }
}
