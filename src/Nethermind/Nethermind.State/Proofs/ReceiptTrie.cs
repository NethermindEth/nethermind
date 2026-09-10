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
public sealed class ReceiptTrie : PatriciaTrie<TxReceipt>
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
        return new ReceiptTrie(spec, receipts, decoder, cappedArrayPool, canBuildProof: true, canBeParallel: canBeParallel).BuildProof(index);
    }

    public static Hash256 CalculateRoot(IReceiptSpec receiptSpec, ReadOnlySpan<TxReceipt> txReceipts, IRlpDecoder<TxReceipt> decoder)
    {
        ArgumentNullException.ThrowIfNull(receiptSpec);
        ArgumentNullException.ThrowIfNull(decoder);
        if (txReceipts.IsEmpty) return Keccak.EmptyTreeHash;

        if (decoder is not ReceiptMessageDecoder receiptDecoder)
        {
            bool canBeParallel = txReceipts.Length > MinItemsForParallelRootHash;
            using TrackingCappedArrayPool pool = new(txReceipts.Length * 4, canBeParallel: canBeParallel);
            return new ReceiptTrie(receiptSpec, txReceipts, decoder, pool, canBeParallel: canBeParallel).RootHash;
        }

        RlpBehaviors behavior = (receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
            | RlpBehaviors.SkipTypedWrapping;
        return new IndexedTrieRoot.Calculator<TxReceipt, ReceiptEncoder>(txReceipts, new(receiptDecoder, behavior)).Calculate();
    }
    private readonly struct ReceiptEncoder(ReceiptMessageDecoder decoder, RlpBehaviors behavior) : IndexedTrieRoot.IValueEncoder<TxReceipt>
    {
        public ReadOnlySpan<byte> GetEncodedValue(TxReceipt item) => default;
        public int GetLength(TxReceipt item) => decoder.GetLength(item, behavior);
        public void Encode<TWriter>(ref TWriter writer, TxReceipt item) where TWriter : struct, IRlpWriteBackend, allows ref struct => decoder.Encode(ref writer, item, behavior);
    }
}
