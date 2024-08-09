// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public class AccessListTxDecoder : IRlpStreamDecoder<Transaction>, IRlpValueDecoder<Transaction>
{
    private readonly AccessListDecoder _decoder = new();
    private readonly bool _lazyHash;

    protected AccessListTxDecoder(bool lazyHash = true)
    {
        _lazyHash = lazyHash;
    }

    protected virtual Transaction NewTx()
    {
        return new Transaction();
    }

    public Transaction? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        Span<byte> transactionSequence = DecodeTxTypeAndGetSequence(rlpStream, rlpBehaviors, out TxType txType);

        Transaction transaction = txType switch
        {
            TxType.AccessList => NewTx(),
            _ => throw new InvalidOperationException("Unexpected TxType")
        };
        transaction.Type = txType;

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        switch (transaction.Type)
        {
            case TxType.AccessList:
                DecodeAccessListPayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                break;
            default:
                throw new InvalidOperationException("Unexpected TxType");
        }

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(rlpStream, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
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

    private static Span<byte> DecodeTxTypeAndGetSequence(RlpStream rlpStream, RlpBehaviors rlpBehaviors, out TxType txType)
    {
        static Span<byte> DecodeTxType(RlpStream rlpStream, int length, out TxType txType)
        {
            Span<byte> sequence = rlpStream.Peek(length);
            txType = (TxType)rlpStream.ReadByte();
            return sequence;
        }

        Span<byte> transactionSequence = rlpStream.PeekNextItem();
        txType = TxType.AccessList;
        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
        {
            byte firstByte = rlpStream.PeekByte();
            if (firstByte <= 0x7f) // it is typed transactions
            {
                transactionSequence = DecodeTxType(rlpStream, rlpStream.Length, out txType);
            }
        }
        else if (!rlpStream.IsSequenceNext())
        {
            transactionSequence = DecodeTxType(rlpStream, rlpStream.ReadPrefixAndContentLength().ContentLength, out txType);
        }

        return transactionSequence;
    }

    private void DecodeAccessListPayloadWithoutSig(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = rlpStream.DecodeULong();
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
        transaction.AccessList = _decoder.Decode(rlpStream, rlpBehaviors);
    }

    private void DecodePayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory();
        transaction.AccessList = _decoder.Decode(ref decoderContext, rlpBehaviors);
    }

    private void EncodePayloadWithoutPayload(Transaction item, RlpStream stream, RlpBehaviors rlpBehaviors)
    {
        stream.Encode(item.ChainId ?? 0);
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
        _decoder.Encode(stream, item.AccessList, rlpBehaviors);
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

        TxType txType = TxType.AccessList;
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
                (int PrefixLength, int ContentLength) prefixAndContentLength = decoderContext.ReadPrefixAndContentLength();
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(prefixAndContentLength.ContentLength);
                txType = (TxType)decoderContext.ReadByte();
            }
        }

        transaction = txType switch
        {
            TxType.AccessList => NewTx(),
            _ => throw new InvalidOperationException("Unexpected TxType")
        };
        transaction.Type = txType;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        switch (transaction.Type)
        {
            case TxType.AccessList:
                DecodePayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
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

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
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
        else
        {
            v += Signature.VOffset;
            Signature signature = new(rBytes, sBytes, v);
            transaction.Signature = signature;
        }
    }

    public Rlp Encode(Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public void Encode(RlpStream stream, Transaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        EncodeTx(stream, item, rlpBehaviors);
    }

    public Rlp EncodeTx(Transaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
        EncodeTx(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    private void EncodeTx(RlpStream stream, Transaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        int contentLength = GetContentLength(item);
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
        {
            stream.StartByteArray(sequenceLength + 1, false);
        }

        stream.WriteByte((byte)item.Type);

        stream.StartSequence(contentLength);

        switch (item.Type)
        {
            case TxType.AccessList:
                EncodePayloadWithoutPayload(item, stream, rlpBehaviors);
                break;
            default:
                throw new InvalidOperationException("Unexpected TxType");
        }

        EncodeSignature(stream, item);
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
            stream.Encode(item.Type == TxType.Legacy ? item.Signature.V : item.Signature.RecoveryId);
            stream.Encode(item.Signature.RAsSpan.WithoutLeadingZeros());
            stream.Encode(item.Signature.SAsSpan.WithoutLeadingZeros());
        }
    }

    private int GetAccessListContentLength(Transaction item)
    {
        return Rlp.LengthOf(item.Nonce)
               + Rlp.LengthOf(item.GasPrice)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.Data)
               + Rlp.LengthOf(item.ChainId ?? 0)
               + _decoder.GetLength(item.AccessList, RlpBehaviors.None);
    }

    private int GetContentLength(Transaction item)
    {
        var contentLength = item.Type switch
        {
            TxType.AccessList => GetAccessListContentLength(item),
            _ => throw new InvalidOperationException("Unexpected TxType"),
        };
        contentLength += GetSignatureContentLength(item);

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
        int txContentLength = GetContentLength(tx);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength); // Rlp(TransactionType || TransactionPayload)
        return result;
    }
}
