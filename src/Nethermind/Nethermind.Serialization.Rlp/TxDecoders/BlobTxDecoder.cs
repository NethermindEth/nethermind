// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class BlobTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseEIP1559TxDecoder<T>(TxType.Blob, transactionFactory) where T : Transaction, new()
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

        base.Decode(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors | RlpBehaviors.ExcludeHashes);

        if (transaction is not null)
        {
            if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
            {
                DecodeShardBlobNetworkWrapper(transaction, ref decoderContext, rlpBehaviors);

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
                {
                    decoderContext.Check(networkWrapperCheck);
                }

                if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
                {
                    transaction.Hash = CalculateHashForNetworkPayloadForm(transactionSequence);
                }
            }
            else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                CalculateHash(transaction, txSequenceStart, transactionSequence, ref decoderContext);
            }
        }
    }

    protected override void EncodeTypedWrapped<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors, bool forSigning, int contentLength)
    {
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            writer.StartSequence(contentLength);
            // if the transaction is in mempool form, we started the mempool form sequence
            // and now we want to encode the non-mempool form contents, so we need to adjust the content length for that encoding
            contentLength = GetContentLength(transaction, rlpBehaviors & ~RlpBehaviors.InMempoolForm, forSigning);
        }

        // this always encodes in non-mempool form
        base.EncodeTypedWrapped(transaction, ref writer, rlpBehaviors, forSigning, contentLength);

        // we encode additional mempool form contents if needed
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            EncodeShardBlobNetworkWrapper(transaction, ref writer, rlpBehaviors);
        }

        static void EncodeShardBlobNetworkWrapper(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors) =>
            ShardBlobNetworkWrapperRlp.Encode(ref writer, (ShardBlobNetworkWrapper)transaction.NetworkWrapper!, rlpBehaviors);
    }

    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.DecodePayload(transaction, ref decoderContext, rlpBehaviors);
        transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
        transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays(BlobVersionedHashesCountLimit, innerSize: Hash256.Size);
    }

    protected override void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.EncodePayload(transaction, ref writer, rlpBehaviors);
        writer.Encode(transaction.MaxFeePerBlobGas!.Value);
        writer.Encode(transaction.BlobVersionedHashes!);
    }

    private static void DecodeShardBlobNetworkWrapper(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) =>
        transaction.NetworkWrapper = ShardBlobNetworkWrapperRlp.Decode(ref decoderContext, rlpBehaviors);

    private static Hash256 CalculateHashForNetworkPayloadForm(ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> txType = [(byte)TxType.Blob];
        hash.Update(txType);
        hash.Update(transactionSequence);
        return new Hash256(hash.GenerateValueHash());
    }

    protected override int GetContentLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = base.GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
            ? GetShardBlobNetworkWrapperLength(transaction, contentLength, rlpBehaviors)
            : contentLength;

        static int GetShardBlobNetworkWrapperLength(Transaction transaction, int txContentLength, RlpBehaviors rlpBehaviors) =>
            Rlp.LengthOfSequence(txContentLength)
            + ShardBlobNetworkWrapperRlp.GetFieldsLength((ShardBlobNetworkWrapper)transaction.NetworkWrapper!, rlpBehaviors);
    }

    protected override int GetPayloadLength(Transaction transaction) =>
        base.GetPayloadLength(transaction)
        + Rlp.LengthOf(transaction.MaxFeePerBlobGas)
        + Rlp.LengthOf(transaction.BlobVersionedHashes);
}
