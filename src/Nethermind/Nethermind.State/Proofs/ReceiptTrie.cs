// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.FluentRlp;
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
        RlpBehaviors behavior = GetBehavior(spec);
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

        RlpBehaviors behavior = GetBehavior(receiptSpec);
        return new IndexedTrieRoot.Calculator<TxReceipt, ReceiptEncoder>(txReceipts, new(receiptDecoder, behavior))
            .Calculate(minItemsForParallel: IndexedTrieRoot.MinReceiptsForParallelRootHash);
    }

    /// <summary>Computes the receipts root of a block's receipts as a receipts message carries them.</summary>
    /// <remarks>
    /// The trie values are the ones <see cref="NetworkReceipt.WriteTrieLeaf"/> writes, which equal the ones
    /// <see cref="ReceiptMessageDecoder"/> writes for the decoded receipts when the message holds them in canonical form.
    /// </remarks>
    /// <param name="blockReceipts">The encoded sequence of the block's receipts, each one <see cref="NetworkReceipt.TryRead"/> reads.</param>
    /// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
    /// <param name="root">The receipts root.</param>
    /// <returns><see langword="false"/> when a receipt has a zero-length bloom before EIP-7668, which the decoder takes as received.</returns>
    /// <exception cref="RlpReaderException">A receipt is not one <see cref="NetworkReceipt.TryRead"/> reads.</exception>
    public static bool TryCalculateRoot(IReceiptSpec spec, ReadOnlySpan<byte> blockReceipts, bool withBloom, out Hash256 root)
    {
        bool eip7668 = spec.IsEip7668Enabled;
        root = Keccak.EmptyTreeHash;
        int count = 0;
        int totalLength = 0;
        RlpReader receipts = new RlpReader(blockReceipts).EnterSequence(out _);
        while (receipts.HasNext)
        {
            NetworkReceipt receipt = NetworkReceipt.ReadValidated(ref receipts, withBloom);
            if (withBloom && !eip7668 && receipt.Bloom.IsEmpty) return false;
            totalLength = checked(totalLength + receipt.GetTrieLeafLength(eip7668));
            count++;
        }

        if (count == 0) return true;

        using ArrayPoolDisposableReturn rental = ArrayPoolDisposableReturn.Rent(totalLength, out byte[] buffer);
        using ArrayPoolList<ReadOnlyMemory<byte>> leaves = new(count, count);
        receipts = new RlpReader(blockReceipts).EnterSequence(out _);
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            NetworkReceipt receipt = NetworkReceipt.ReadValidated(ref receipts, withBloom);
            int length = receipt.GetTrieLeafLength(eip7668);
            scoped RlpWriter writer = RlpWriter.ContentWriter(buffer.AsSpan(offset, length));
            receipt.WriteTrieLeaf(ref writer, eip7668);
            leaves[i] = buffer.AsMemory(offset, length);
            offset += length;
        }

        root = new IndexedTrieRoot.Calculator<ReadOnlyMemory<byte>, EncodedLeafEncoder>(leaves.AsSpan(), default)
            .Calculate(minItemsForParallel: IndexedTrieRoot.MinReceiptsForParallelRootHash);
        return true;
    }

    private readonly struct EncodedLeafEncoder : IndexedTrieRoot.IValueEncoder<ReadOnlyMemory<byte>>
    {
        public IndexedTrieRoot.LeafBatching Batching => IndexedTrieRoot.LeafBatching.Encoded;
        public ReadOnlySpan<byte> GetEncodedValue(ReadOnlyMemory<byte> item) => item.Span;
        public int GetLength(ReadOnlyMemory<byte> item) => item.Length;
        public void Encode(ref RlpWriter writer, ReadOnlyMemory<byte> item) => writer.WriteEncoded(item.Span);
    }

    private static RlpBehaviors GetBehavior(IReceiptSpec spec) =>
        (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
        | (spec.IsEip7668Enabled ? RlpBehaviors.Eip7668Receipts : RlpBehaviors.None)
        | RlpBehaviors.SkipTypedWrapping;

    private readonly struct ReceiptEncoder(ReceiptMessageDecoder decoder, RlpBehaviors behavior) : IndexedTrieRoot.IValueEncoder<TxReceipt>
    {
        public IndexedTrieRoot.LeafBatching Batching => IndexedTrieRoot.LeafBatching.MultiBlock;
        public ReadOnlySpan<byte> GetEncodedValue(TxReceipt item) => default;
        public int GetLength(TxReceipt item) => decoder.GetLength(item, behavior);
        public void Encode(ref RlpWriter writer, TxReceipt item) => decoder.Encode(ref writer, item, behavior);
    }
}
