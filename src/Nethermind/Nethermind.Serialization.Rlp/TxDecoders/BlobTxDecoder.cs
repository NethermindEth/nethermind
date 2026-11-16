// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class BlobTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseEIP1559TxDecoder(TxType.Blob, transactionFactory)
{
    public static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(ShardBlobNetworkWrapperRlp.BlobCountLimit, nameof(Transaction.BlobVersionedHashes));

    public override void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == 0)
        {
            DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
        }
        else if (rlpBehaviors.IsTrustedInput())
        {
            DecodeNetworkWrapper<OffFlag>(ref transaction, ref reader, source, rlpBehaviors);
        }
        else
        {
            DecodeNetworkWrapper<OnFlag>(ref transaction, ref reader, source, rlpBehaviors);
        }
    }

    private void DecodeNetworkWrapper<TCanonicalChecks>(ref Transaction? transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader wrapper = reader.EnterSequence<TCanonicalChecks>(out int wrapperLength);
        RlpReader peek = wrapper;
        ReadOnlySpan<byte> transactionSequence = peek.ReadEncodedItem<TCanonicalChecks>();

        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref wrapper, source, rlpBehaviors | RlpBehaviors.ExcludeHashes);

        transaction!.NetworkWrapper = ShardBlobNetworkWrapperRlp.Decode<TCanonicalChecks>(ref wrapper, rlpBehaviors);
        reader.ExitSequence(in wrapper, wrapperLength, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            transaction.Hash = NetworkPayloadFormHash.Calculate(TxType.Blob, transactionSequence);
        }
    }

    /// <summary>Writes the network form of a type-3 transaction from its stored record, without decoding it.</summary>
    /// <param name="record">
    /// The record that <see cref="TxDecoder"/> encodes with <see cref="RlpBehaviors.InMempoolForm"/> and
    /// <see cref="RlpBehaviors.Storage"/>.
    /// </param>
    /// <param name="networkForm">
    /// The encoding with <see cref="RlpBehaviors.InMempoolForm"/> and <see cref="RlpBehaviors.SkipTypedWrapping"/>.
    /// The caller disposes it.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the record is empty, is not a type-3 transaction, does not hold every blob, or is
    /// not written again byte for byte.
    /// </returns>
    /// <remarks>
    /// The transaction and sidecar items are copied as they are and the storage-only items after them are dropped, so
    /// only the type byte and the sequence prefix are written again.
    /// </remarks>
    public static bool TryGetNetworkForm(ReadOnlySpan<byte> record, [NotNullWhen(true)] out IOwnedReadOnlyList<byte>? networkForm)
    {
        networkForm = null;
        if (record.IsEmpty) return false;

        ReadOnlySpan<byte> typedTx = new RlpReader(record).ReadBytes();
        if (typedTx.IsEmpty || typedTx[0] != (byte)TxType.Blob) return false;

        RlpReader items = new RlpReader(typedTx[1..]).EnterSequence(out _);
        RlpReader all = items;
        ReadOnlySpan<byte> storedItems = all.ReadRaw(all.Remaining);
        items.SkipItem();
        if (!ShardBlobNetworkWrapperRlp.TrySkipNetworkItems(ref items)) return false;

        ReadOnlySpan<byte> networkItems = storedItems[..items.BytesRead];
        int length = 1 + Rlp.LengthOfSequence(networkItems.Length);
        ArrayPoolList<byte> encoded = new(SafeArrayPool<byte>.Shared, length, length, clearFirst: false);
        RlpWriter writer = RlpWriter.ContentWriter(encoded.AsSpan());
        writer.WriteByte((byte)TxType.Blob);
        RlpSequence sequence = writer.StartSequence(networkItems.Length);
        writer.WriteEncoded(networkItems);
        writer.EndSequence(sequence);
        networkForm = encoded;
        return true;
    }

    public override void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors, forSigning);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors, forSigning);

    internal static void EncodeTransaction(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int contentLength = GetContentLength(transaction, forSigning);
        ShardBlobNetworkWrapper? wrapper = (rlpBehaviors & RlpBehaviors.InMempoolForm) != 0 ? GetNetworkWrapper(transaction) : null;
        int bodyContentLength = wrapper is null ? contentLength : GetShardBlobNetworkWrapperLength(wrapper, contentLength, rlpBehaviors);

        StartTypedTransaction(ref writer, TxType.Blob, Rlp.LengthOfSequence(bodyContentLength), rlpBehaviors);

        // The mempool form wraps the canonical payload together with the sidecar.
        RlpSequence wrapperSequence = wrapper is not null ? writer.StartSequence(bodyContentLength) : default;

        RlpSequence sequence = writer.StartSequence(contentLength);
        EncodeEip1559Fields(transaction, ref writer, rlpBehaviors);
        writer.Encode(GetMaxFeePerBlobGas(transaction));
        EncodeBlobVersionedHashes(ref writer, GetBlobVersionedHashes(transaction));
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
        writer.EndSequence(sequence);

        if (wrapper is not null)
        {
            ShardBlobNetworkWrapperRlp.Encode(ref writer, wrapper, rlpBehaviors);
            writer.EndSequence(wrapperSequence);
        }
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int contentLength = GetContentLength(transaction, forSigning);
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) != 0)
        {
            contentLength = GetShardBlobNetworkWrapperLength(GetNetworkWrapper(transaction), contentLength, rlpBehaviors);
        }

        return GetTypedTransactionLength(Rlp.LengthOfSequence(contentLength), rlpBehaviors);
    }

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        GetEip1559FieldsLength(transaction)
        + Rlp.LengthOf(GetMaxFeePerBlobGas(transaction))
        + GetBlobVersionedHashesLength(GetBlobVersionedHashes(transaction))
        + GetTypedSignatureLength(transaction.Signature, forSigning);

    private static int GetShardBlobNetworkWrapperLength(ShardBlobNetworkWrapper wrapper, int txContentLength, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(txContentLength) + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper, rlpBehaviors);

    private static UInt256 GetMaxFeePerBlobGas(Transaction transaction) =>
        transaction.MaxFeePerBlobGas
        ?? throw new RlpReaderException($"{nameof(Transaction.MaxFeePerBlobGas)} is required for blob transaction RLP.");

    private static byte[]?[] GetBlobVersionedHashes(Transaction transaction) =>
        transaction.BlobVersionedHashes
        ?? throw new RlpReaderException($"{nameof(Transaction.BlobVersionedHashes)} is required for blob transaction RLP.");

    private static ShardBlobNetworkWrapper GetNetworkWrapper(Transaction transaction) =>
        transaction.NetworkWrapper as ShardBlobNetworkWrapper
        ?? throw new RlpReaderException($"{nameof(Transaction.NetworkWrapper)} must be {nameof(ShardBlobNetworkWrapper)} for mempool blob transaction RLP.");

    private static int GetBlobVersionedHashesLength(byte[]?[] blobVersionedHashes) =>
        Rlp.LengthOfSequence(GetBlobVersionedHashesContentLength(blobVersionedHashes));

    private static int GetBlobVersionedHashesContentLength(byte[]?[] blobVersionedHashes)
    {
        int contentLength = 0;
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            byte[] hash = blobVersionedHashes[i]
                ?? throw new RlpReaderException($"{nameof(Transaction.BlobVersionedHashes)} contains a null versioned hash.");
            contentLength += Rlp.LengthOf(hash);
        }

        return contentLength;
    }

    private static void EncodeBlobVersionedHashes(ref RlpWriter writer, byte[]?[] blobVersionedHashes)
    {
        RlpSequence sequence = writer.StartSequence(GetBlobVersionedHashesContentLength(blobVersionedHashes));
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            writer.Encode(blobVersionedHashes[i]);
        }

        writer.EndSequence(sequence);
    }

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag
        {
            DecodeEip1559Fields<TCanonicalChecks>(transaction, ref reader, source, rlpBehaviors);
            transaction.MaxFeePerBlobGas = reader.DecodeUInt256<TCanonicalChecks>();
            transaction.BlobVersionedHashes = DecodeVersionedHashes<TCanonicalChecks>(ref reader, BlobVersionedHashesCountLimit);
        }

        public static void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeTypedSignature<TCanonicalChecks>(transaction, ref reader, rlpBehaviors);
    }
}
