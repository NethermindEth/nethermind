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

        DecodeShardBlobPayloadWithoutSig(transaction, rlpStream, rlpBehaviors);

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

    private void DecodeShardBlobPayloadWithoutSig(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
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

    private static void DecodeShardBlobNetworkPayload(Transaction transaction, RlpStream rlpStream)
    {
        byte[][] blobs = rlpStream.DecodeByteArrays();
        byte[][] commitments = rlpStream.DecodeByteArrays();
        byte[][] proofs = rlpStream.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private void DecodeShardBlobPayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
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

    private static void DecodeShardBlobNetworkPayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        byte[][] blobs = decoderContext.DecodeByteArrays();
        byte[][] commitments = decoderContext.DecodeByteArrays();
        byte[][] proofs = decoderContext.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private void EncodeShardBlobPayloadWithoutPayload(Transaction item, RlpStream stream, RlpBehaviors rlpBehaviors)
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

    public Transaction? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Transaction transaction = null;
        Decode(ref decoderContext, ref transaction, rlpBehaviors);

        return transaction;
    }


    public void Decode(ref Rlp.ValueDecoderContext decoderContext, ref Transaction? transaction, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();
            transaction = null;
            return;
        }

        int txSequenceStart = decoderContext.Position;
        ReadOnlySpan<byte> transactionSequence = decoderContext.PeekNextItem();

        TxType txType = TxType.Legacy;
        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
        {
            byte firstByte = decoderContext.PeekByte();
            if (firstByte <= 0x7f) // it is typed transactions
            {
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(decoderContext.Length);
                txType = (TxType)decoderContext.ReadByte();
            }
        }
        else
        {
            if (!decoderContext.IsSequenceNext())
            {
                (int _, int ContentLength) = decoderContext.ReadPrefixAndContentLength();
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(ContentLength);
                txType = (TxType)decoderContext.ReadByte();
            }
        }

        transaction = txType switch
        {
            TxType.Blob => new(),
            _ => throw new InvalidOperationException("Unexpected TxType")
        };
        transaction.Type = txType;

        int networkWrapperCheck = 0;
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            int networkWrapperLength = decoderContext.ReadSequenceLength();
            networkWrapperCheck = decoderContext.Position + networkWrapperLength;
            int rlpRength = decoderContext.PeekNextRlpLength();
            txSequenceStart = decoderContext.Position;
            transactionSequence = decoderContext.Peek(rlpRength);
        }

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        switch (transaction.Type)
        {
            case TxType.Blob:
                DecodeShardBlobPayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                break;
            default:
                throw new InvalidOperationException("Unexpected TxType");
        }

        if (decoderContext.Position < lastCheck)
        {
            DecodeSignature(ref decoderContext, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            DecodeShardBlobNetworkPayload(transaction, ref decoderContext);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
            {
                decoderContext.Check(networkWrapperCheck);
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
                if (decoderContext.ShouldSliceMemory)
                {
                    // Do not copy the memory in this case.
                    int currentPosition = decoderContext.Position;
                    decoderContext.Position = txSequenceStart;
                    transaction.SetPreHashMemoryNoLock(decoderContext.ReadMemory(transactionSequence.Length));
                    decoderContext.Position = currentPosition;
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
    }

    private static void DecodeSignature(RlpStream rlpStream, RlpBehaviors rlpBehaviors, Transaction transaction)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        ApplySignature(transaction, v, rBytes, sBytes, rlpBehaviors);
    }

    private static void DecodeSignature(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors, Transaction transaction)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        ApplySignature(transaction, v, rBytes, sBytes, rlpBehaviors);
    }

    private static void ApplySignature(Transaction transaction, ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, RlpBehaviors rlpBehaviors)
    {
        bool allowUnsigned = (rlpBehaviors & RlpBehaviors.AllowUnsigned) == RlpBehaviors.AllowUnsigned;
        bool isSignatureOk = true;
        string signatureError = null;
        if (rBytes.Length == 0 || sBytes.Length == 0)
        {
            isSignatureOk = false;
            signatureError = "VRS is 0 length when decoding Transaction";
        }
        else if (rBytes[0] == 0 || sBytes[0] == 0)
        {
            isSignatureOk = false;
            signatureError = "VRS starting with 0";
        }
        else if (rBytes.Length > 32 || sBytes.Length > 32)
        {
            isSignatureOk = false;
            signatureError = "R and S lengths expected to be less or equal 32";
        }
        else if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
        {
            isSignatureOk = false;
            signatureError = "Both 'r' and 's' are zero when decoding a transaction.";
        }

        if (!isSignatureOk && !allowUnsigned)
        {
            throw new RlpException(signatureError);
        }

        v += Signature.VOffset;
        Signature signature = new(rBytes, sBytes, v);
        transaction.Signature = signature;
    }

    public Rlp Encode(Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public void Encode(RlpStream stream, Transaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Encode(item, stream, rlpBehaviors);
    }

    public Rlp EncodeTx(Transaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(item, rlpStream, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
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

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            stream.StartSequence(contentLength);
            contentLength = GetContentLength(item);
        }

        stream.StartSequence(contentLength);

        switch (item.Type)
        {
            case TxType.Blob:
                EncodeShardBlobPayloadWithoutPayload(item, stream, rlpBehaviors);
                break;
            default:
                throw new InvalidOperationException("Unexpected TxType");
        }

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

    private static void EncodeShardBlobNetworkPayload(Transaction transaction, RlpStream rlpStream)
    {
        ShardBlobNetworkWrapper networkWrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        rlpStream.Encode(networkWrapper.Blobs);
        rlpStream.Encode(networkWrapper.Commitments);
        rlpStream.Encode(networkWrapper.Proofs);
    }

    private int GetShardBlobContentLength(Transaction item)
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
        int contentLength = item.Type switch
        {
            TxType.Blob => GetShardBlobContentLength(item),
            _ => throw new InvalidOperationException("Unexpected TxType"),
        };
        contentLength += GetSignatureContentLength(item);

        if (withNetworkWrapper)
        {
            contentLength = GetShardBlobNetworkWrapperContentLength(item, contentLength); // TODO: shouldnt' this be `+=` ?
        }
        return contentLength;
    }

    private static int GetSignatureContentLength(Transaction item)
    {
        int contentLength = 0;

        bool signatureIsNull = item.Signature is null;
        contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.RecoveryId);
        contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
        contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());

        return contentLength;
    }

    public int GetLength(Transaction tx, RlpBehaviors rlpBehaviors)
    {
        int txContentLength = GetContentLength(tx, withNetworkWrapper: (rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength); // Rlp(TransactionType || TransactionPayload)
        return result;
    }
}
