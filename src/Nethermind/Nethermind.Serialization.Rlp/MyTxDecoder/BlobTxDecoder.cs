// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class BlobTxDecoder(bool lazyHash = true) : AbstractTxDecoder
{
    private readonly AccessListDecoder _accessListDecoder = new();
    private readonly bool _lazyHash = lazyHash;

    public override Transaction Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.Blob
        };

        int positionAfterNetworkWrapper = 0;
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            int networkWrapperLength = rlpStream.ReadSequenceLength();
            positionAfterNetworkWrapper = rlpStream.Position + networkWrapperLength;
            int rlpLength = rlpStream.PeekNextRlpLength();
            transactionSequence = rlpStream.Peek(rlpLength);
        }

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayloadWithoutSig(transaction, rlpStream, rlpBehaviors);

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(rlpStream, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            DecodeShardBlobNetworkPayload(transaction, rlpStream);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
            {
                rlpStream.Check(positionAfterNetworkWrapper);
            }

            if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                transaction.Hash = CalculateHashForNetworkPayloadForm(transaction.Type, transactionSequence);
            }
        }
        else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize && _lazyHash)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                transaction.SetPreHashNoLock(transactionSequence);
            }
            else
            {
                // Just calculate the Hash as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
        }

        return transaction;
    }

    private static Hash256 CalculateHashForNetworkPayloadForm(TxType type, ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> txType = [(byte)type];
        hash.Update(txType);
        hash.Update(transactionSequence);
        return new Hash256(hash.Hash);
    }

    private void DecodePayloadWithoutSig(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = rlpStream.DecodeULong();
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256(); // gas premium
        transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
        transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        transaction.MaxFeePerBlobGas = rlpStream.DecodeUInt256();
        transaction.BlobVersionedHashes = rlpStream.DecodeByteArrays();
    }

    private void DecodePayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256(); // gas premium
        transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArray();
        transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
        transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays();
    }

    private static void DecodeShardBlobNetworkPayload(Transaction transaction, RlpStream rlpStream)
    {
        byte[][] blobs = rlpStream.DecodeByteArrays();
        byte[][] commitments = rlpStream.DecodeByteArrays();
        byte[][] proofs = rlpStream.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private static void DecodeShardBlobNetworkPayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        byte[][] blobs = decoderContext.DecodeByteArrays();
        byte[][] commitments = decoderContext.DecodeByteArrays();
        byte[][] proofs = decoderContext.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private void EncodePayloadWithoutSignature(Transaction item, RlpStream stream, RlpBehaviors rlpBehaviors)
    {
        stream.Encode(item.ChainId ?? 0);
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice); // gas premium
        stream.Encode(item.DecodedMaxFeePerGas);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
        _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
        stream.Encode(item.MaxFeePerBlobGas.Value);
        stream.Encode(item.BlobVersionedHashes);
    }

    private static void EncodeShardBlobNetworkPayload(Transaction transaction, RlpStream rlpStream)
    {
        ShardBlobNetworkWrapper networkWrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        rlpStream.Encode(networkWrapper.Blobs);
        rlpStream.Encode(networkWrapper.Commitments);
        rlpStream.Encode(networkWrapper.Proofs);
    }

    public override Transaction Decode(int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.Blob,

        };

        int networkWrapperCheck = 0;
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            int networkWrapperLength = context.ReadSequenceLength();
            networkWrapperCheck = context.Position + networkWrapperLength;
            int rlpRength = context.PeekNextRlpLength();
            txSequenceStart = context.Position;
            transactionSequence = context.Peek(rlpRength);
        }

        int transactionLength = context.ReadSequenceLength();
        int lastCheck = context.Position + transactionLength;

        DecodePayloadWithoutSig(transaction, ref context, rlpBehaviors);

        if (context.Position < lastCheck)
        {
            DecodeSignature(ref context, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            context.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            DecodeShardBlobNetworkPayload(transaction, ref context);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
            {
                context.Check(networkWrapperCheck);
            }

            if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                transaction.Hash = CalculateHashForNetworkPayloadForm(transaction.Type, transactionSequence);
            }
        }
        else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize && _lazyHash)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                if (context.ShouldSliceMemory)
                {
                    // Do not copy the memory in this case.
                    int currentPosition = context.Position;
                    context.Position = txSequenceStart;
                    transaction.SetPreHashMemoryNoLock(context.ReadMemory(transactionSequence.Length));
                    context.Position = currentPosition;
                }
                else
                {
                    transaction.SetPreHashNoLock(transactionSequence);
                }
            }
            else
            {
                // Just calculate the Hash immediately as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
        }

        return transaction;
    }

    private static void DecodeSignature(RlpStream rlpStream, RlpBehaviors rlpBehaviors, Transaction transaction)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors);
    }

    private static void DecodeSignature(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors, Transaction transaction)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors);
    }

    public override Rlp EncodeTx(Transaction? item, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item?.Type != TxType.Blob) { throw new InvalidOperationException("Unexpected TxType"); }

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(item, rlpStream, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item?.Type != TxType.Blob) { throw new InvalidOperationException("Unexpected TxType"); }

        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        int contentLength = GetContentLength(item, (rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm);
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
        {
            stream.StartByteArray(sequenceLength + 1, false);
        }

        stream.WriteByte((byte)item.Type);

        // TODO: I think we can remove this block completely
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            stream.StartSequence(contentLength);
            contentLength = GetContentLength(item);
        }

        stream.StartSequence(contentLength);

        EncodePayloadWithoutSignature(item, stream, rlpBehaviors);
        EncodeSignature(stream, item);

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            EncodeShardBlobNetworkPayload(item, stream);
        }
    }

    private static void EncodeSignature(RlpStream stream, Transaction item)
    {
        if (item.Signature is null)
        {
            stream.Encode(0);
            stream.Encode(Bytes.Empty);
            stream.Encode(Bytes.Empty);
        }
        else
        {
            stream.Encode(item.Signature.RecoveryId);
            stream.Encode(item.Signature.RAsSpan.WithoutLeadingZeros());
            stream.Encode(item.Signature.SAsSpan.WithoutLeadingZeros());
        }
    }

    private int GetPayloadContentLength(Transaction item)
    {
        return Rlp.LengthOf(item.Nonce)
               + Rlp.LengthOf(item.GasPrice) // gas premium
               + Rlp.LengthOf(item.DecodedMaxFeePerGas)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.Data)
               + Rlp.LengthOf(item.ChainId ?? 0)
               + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None)
               + Rlp.LengthOf(item.MaxFeePerBlobGas)
               + Rlp.LengthOf(item.BlobVersionedHashes);
    }

    private static int GetShardBlobNetworkWrapperContentLength(Transaction item, int txContentLength)
    {
        ShardBlobNetworkWrapper networkWrapper = item.NetworkWrapper as ShardBlobNetworkWrapper;
        return Rlp.LengthOfSequence(txContentLength)
               + Rlp.LengthOf(networkWrapper.Blobs)
               + Rlp.LengthOf(networkWrapper.Commitments)
               + Rlp.LengthOf(networkWrapper.Proofs);
    }

    private int GetContentLength(Transaction item, bool withNetworkWrapper = false)
    {
        int contentLength = GetPayloadContentLength(item);
        contentLength += GetSignatureContentLength(item);
        if (withNetworkWrapper)
        {
            // TODO: shouldnt' this be `+=` ?
            contentLength = GetShardBlobNetworkWrapperContentLength(item, contentLength);
        }
        return contentLength;
    }

    private static int GetSignatureContentLength(Transaction item)
    {
        int contentLength = 0;
        if (item.Signature is null)
        {
            contentLength += 1;
            contentLength += 1;
            contentLength += 1;
        }
        else
        {
            contentLength += Rlp.LengthOf(item.Signature.RecoveryId);
            contentLength += Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
            contentLength += Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());
        }

        return contentLength;
    }

    public override int GetLength(Transaction tx, RlpBehaviors rlpBehaviors)
    {
        if (tx?.Type != TxType.Blob) { throw new InvalidOperationException("Unexpected TxType"); }

        int txContentLength = GetContentLength(tx, withNetworkWrapper: (rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength); // Rlp(TransactionType || TransactionPayload)
        return result;
    }
}
