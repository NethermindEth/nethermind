// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class AccessListTxDecoder(bool lazyHash = true) : AbstractTxDecoder
{
    private readonly AccessListDecoder _decoder = new();

    public override Transaction Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.AccessList
        };

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

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (lazyHash && transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize)
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

    private void DecodePayloadWithoutSig(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
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

    private void EncodePayloadWithoutSignature(Transaction item, RlpStream stream, RlpBehaviors rlpBehaviors)
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

    public override Transaction Decode(int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.AccessList
        };

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);

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
            if (lazyHash && transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize)
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
        if (item?.Type != TxType.AccessList) { throw new InvalidOperationException("Unexpected TxType"); }

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(item, rlpStream, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item?.Type != TxType.AccessList) { throw new InvalidOperationException("Unexpected TxType"); }

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
        EncodePayloadWithoutSignature(item, stream, rlpBehaviors);
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
            stream.Encode(item.Signature.RecoveryId);
            stream.Encode(item.Signature.RAsSpan.WithoutLeadingZeros());
            stream.Encode(item.Signature.SAsSpan.WithoutLeadingZeros());
        }
    }

    private int GetContentLength(Transaction item)
    {
        return GetPayloadContentLength(item) + GetSignatureContentLength(item);
    }

    private int GetPayloadContentLength(Transaction item)
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
        if (tx?.Type != TxType.AccessList) { throw new InvalidOperationException("Unexpected TxType"); }

        int txContentLength = GetContentLength(tx);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength); // Rlp(TransactionType || TransactionPayload)
        return result;
    }
}
