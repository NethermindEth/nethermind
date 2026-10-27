// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class BlobTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseEIP1559TxDecoder(TxType.Blob, transactionFactory)
{
    public static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(ShardBlobNetworkWrapperRlp.BlobCountLimit, nameof(Transaction.BlobVersionedHashes));

    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int networkWrapperCheck = 0;
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            int networkWrapperLength = decoderContext.ReadSequenceLength();
            networkWrapperCheck = decoderContext.Position + networkWrapperLength;
            int rlpLength = decoderContext.PeekNextRlpLength();
            txSequenceStart = decoderContext.Position;
            transactionSequence = decoderContext.Peek(rlpLength);
        }

        DecodeTransaction<Payload>(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors | RlpBehaviors.ExcludeHashes);

        if (transaction is not null)
        {
            if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
            {
                DecodeShardBlobNetworkWrapper(transaction, ref decoderContext, rlpBehaviors, networkWrapperCheck);

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
                {
                    decoderContext.Check(networkWrapperCheck);
                }

                if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
                {
                    transaction.Hash = NetworkPayloadFormHash.Calculate(TxType.Blob, transactionSequence);
                }
            }
            else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                CalculateHash(transaction, txSequenceStart, transactionSequence, ref decoderContext);
            }
        }
    }

    public override void PopulateLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        PopulateTransactionLength(transaction, ref lengths, rlpBehaviors, forSigning);

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, ref lengths, rlpBehaviors, forSigning);

    internal static void EncodeTransaction<TWriter>(Transaction transaction, ref TWriter writer, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int bodyContentLength = lengths.Pop();
        ShardBlobNetworkWrapper? wrapper = (rlpBehaviors & RlpBehaviors.InMempoolForm) != 0 ? GetNetworkWrapper(transaction) : null;

        StartTypedTransaction(ref writer, TxType.Blob, Rlp.LengthOfSequence(bodyContentLength), rlpBehaviors);

        // The mempool form wraps the canonical payload together with the sidecar.
        int contentLength = bodyContentLength;
        if (wrapper is not null)
        {
            writer.StartSequence(bodyContentLength);
            contentLength = lengths.Pop();
        }

        writer.StartSequence(contentLength);
        EncodeEip1559Fields(transaction, ref writer, ref lengths);
        writer.Encode(GetMaxFeePerBlobGas(transaction));
        EncodeBlobVersionedHashes(ref writer, GetBlobVersionedHashes(transaction));
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);

        if (wrapper is not null)
        {
            ShardBlobNetworkWrapperRlp.Encode(ref writer, wrapper, rlpBehaviors);
        }
    }

    internal static void PopulateTransactionLength(Transaction transaction, ref LengthStack lengths, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int contentLength = PopulateEip1559FieldsLength(transaction, ref lengths)
            + Rlp.LengthOf(GetMaxFeePerBlobGas(transaction))
            + GetBlobVersionedHashesLength(GetBlobVersionedHashes(transaction))
            + GetTypedSignatureLength(transaction.Signature, forSigning);

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) != 0)
        {
            lengths.Push(contentLength);
            contentLength = GetShardBlobNetworkWrapperLength(GetNetworkWrapper(transaction), contentLength, rlpBehaviors);
        }

        PushTypedTransactionLength(contentLength, ref lengths, rlpBehaviors);
    }

    private static void DecodeShardBlobNetworkWrapper(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors, int networkWrapperCheck) =>
        transaction.NetworkWrapper = ShardBlobNetworkWrapperRlp.Decode(ref decoderContext, networkWrapperCheck, rlpBehaviors);

    private static int GetShardBlobNetworkWrapperLength(ShardBlobNetworkWrapper wrapper, int txContentLength, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(txContentLength) + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper, rlpBehaviors);

    private static UInt256 GetMaxFeePerBlobGas(Transaction transaction) =>
        transaction.MaxFeePerBlobGas
        ?? throw new RlpException($"{nameof(Transaction.MaxFeePerBlobGas)} is required for blob transaction RLP.");

    private static byte[]?[] GetBlobVersionedHashes(Transaction transaction) =>
        transaction.BlobVersionedHashes
        ?? throw new RlpException($"{nameof(Transaction.BlobVersionedHashes)} is required for blob transaction RLP.");

    private static ShardBlobNetworkWrapper GetNetworkWrapper(Transaction transaction) =>
        transaction.NetworkWrapper as ShardBlobNetworkWrapper
        ?? throw new RlpException($"{nameof(Transaction.NetworkWrapper)} must be {nameof(ShardBlobNetworkWrapper)} for mempool blob transaction RLP.");

    private static int GetBlobVersionedHashesLength(byte[]?[] blobVersionedHashes)
    {
        int contentLength = 0;
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            byte[] hash = blobVersionedHashes[i]
                ?? throw new RlpException($"{nameof(Transaction.BlobVersionedHashes)} contains a null versioned hash.");
            contentLength += Rlp.LengthOf(hash);
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    private static void EncodeBlobVersionedHashes<TWriter>(ref TWriter writer, byte[]?[] blobVersionedHashes)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = 0;
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            byte[] hash = blobVersionedHashes[i]
                ?? throw new RlpException($"{nameof(Transaction.BlobVersionedHashes)} contains a null versioned hash.");
            contentLength += Rlp.LengthOf(hash);
        }

        writer.StartSequence(contentLength);
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            byte[] hash = blobVersionedHashes[i]
                ?? throw new RlpException($"{nameof(Transaction.BlobVersionedHashes)} contains a null versioned hash.");
            writer.Encode(hash);
        }
    }

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload(Transaction transaction, ref RlpReader decoderContext, int payloadEnd, RlpBehaviors rlpBehaviors)
        {
            DecodeEip1559Fields(transaction, ref decoderContext, rlpBehaviors);
            transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
            transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays(BlobVersionedHashesCountLimit, innerSize: Hash256.Size);
        }

        public static void DecodeTrailing(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
            DecodeTypedSignature(transaction, ref decoderContext, rlpBehaviors);
    }
}
