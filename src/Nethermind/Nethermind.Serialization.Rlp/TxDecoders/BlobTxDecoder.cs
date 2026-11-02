// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
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
        else if ((rlpBehaviors & RlpBehaviors.TrustedInput) != 0)
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
