// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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
public sealed partial class ReceiptTrie : PatriciaTrie<TxReceipt>
{
    private readonly IRlpDecoder<TxReceipt> _decoder;
    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    public ReceiptTrie(IReceiptSpec spec, ReadOnlySpan<TxReceipt> receipts, IRlpDecoder<TxReceipt> trieDecoder, ICappedArrayPool bufferPool, bool canBuildProof = false, bool canBeParallel = true)
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

        foreach (TxReceipt receipt in receipts)
        {
            CappedArray<byte> buffer = _decoder.EncodeToCappedArray(receipt, rlpBehaviors: behavior, bufferPool: _bufferPool);
            CappedArray<byte> keyBuffer = Rlp.EncodeToCappedArray(key, _bufferPool);
            key++;

            Set(keyBuffer.AsSpan(), buffer);
        }
    }

    protected override void Initialize(ReadOnlySpan<TxReceipt> list) => throw new NotSupportedException();

    public static byte[][] CalculateReceiptProofs(IReleaseSpec spec, ReadOnlySpan<TxReceipt> receipts, int index, IRlpDecoder<TxReceipt> decoder)
    {
        bool canBeParallel = receipts.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool cappedArrayPool = new(receipts.Length * 4, canBeParallel: canBeParallel);
        ReceiptTrie trie = new(spec, receipts, decoder, cappedArrayPool, canBuildProof: true, canBeParallel: canBeParallel);
        Debug.Assert(trie.RootHash == CalculateRoot(spec, receipts, decoder), "Receipt proofs and streamed roots must agree.");
        return trie.BuildProof(index);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, ReadOnlySpan<TxReceipt> txReceipts, IRlpDecoder<TxReceipt> decoder)
    {
        ArgumentNullException.ThrowIfNull(receiptSpec);
        ArgumentNullException.ThrowIfNull(decoder);
        if (txReceipts.IsEmpty) return Keccak.EmptyTreeHash;

        // Custom codecs may encode an empty value, which the mutable trie treats as deletion.
        // The streamed builder requires ReceiptMessageDecoder's nonempty encodings, including null receipts.
        if (decoder is not ReceiptMessageDecoder receiptDecoder)
        {
            bool canBeParallel = txReceipts.Length > MinItemsForParallelRootHash;
            using TrackingCappedArrayPool pool = new(txReceipts.Length * 4, canBeParallel: canBeParallel);
            return new ReceiptTrie(receiptSpec, txReceipts, decoder, pool, canBeParallel: canBeParallel).RootHash;
        }

        RlpBehaviors behavior = (receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
            | RlpBehaviors.SkipTypedWrapping;
        return new RootCalculator(txReceipts, receiptDecoder, behavior).Calculate();
    }
}
