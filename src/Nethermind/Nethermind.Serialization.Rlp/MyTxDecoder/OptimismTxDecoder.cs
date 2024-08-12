// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Optimism;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class OptimismTxDecoder(bool lazyHash = true) : AbstractTxDecoder
{
    private readonly bool _lazyHash = lazyHash;

    public override Transaction Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        DepositTransaction transaction = new()
        {
            Type = TxType.DepositTx
        };

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodeDepositPayloadWithoutSig(transaction, rlpStream);

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

    public override DepositTransaction Decode(int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        DepositTransaction transaction = new()
        {
            Type = TxType.DepositTx
        };

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodeDepositPayloadWithoutSig(transaction, ref decoderContext);

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

        return transaction;
    }

    private static void DecodeSignature(RlpStream rlpStream, RlpBehaviors rlpBehaviors, DepositTransaction transaction)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();

        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors);
    }

    private static void DecodeSignature(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors, DepositTransaction transaction)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();

        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors);
    }

    public Rlp Encode(DepositTransaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public void Encode(RlpStream stream, DepositTransaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Encode(item, stream, rlpBehaviors);
    }

    public Rlp EncodeTx(DepositTransaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(item, rlpStream);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(Transaction? _wrongType, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // TODO: Deal with subtyping
        DepositTransaction item = (DepositTransaction)_wrongType;

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
            case TxType.DepositTx:
                EncodeDepositTxPayloadWithoutPayload(item, stream);
                break;
            default:
                throw new InvalidOperationException("Unexpected TxType");
        }
    }

    private int GetContentLength(DepositTransaction item)
    {
        var contentLength = item.Type switch
        {
            TxType.DepositTx => GetDepositTxContentLength(item),
            _ => throw new InvalidOperationException("Unexpected TxType"),
        };
        return contentLength;
    }

    public override int GetLength(Transaction _wrongType, RlpBehaviors rlpBehaviors)
    {
        // TODO: Deal with subtyping
        DepositTransaction tx = (DepositTransaction)_wrongType;
        int txContentLength = GetContentLength(tx);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength); // Rlp(TransactionType || TransactionPayload)
        return result;
    }

    public static void DecodeDepositPayloadWithoutSig(DepositTransaction transaction, RlpStream rlpStream)
    {
        transaction.SourceHash = rlpStream.DecodeKeccak();
        transaction.SenderAddress = rlpStream.DecodeAddress();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Mint = rlpStream.DecodeUInt256();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.IsOPSystemTransaction = rlpStream.DecodeBool();
        transaction.Data = rlpStream.DecodeByteArray();
    }

    public static void DecodeDepositPayloadWithoutSig(DepositTransaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        transaction.SourceHash = decoderContext.DecodeKeccak();
        transaction.SenderAddress = decoderContext.DecodeAddress();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Mint = decoderContext.DecodeUInt256();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.IsOPSystemTransaction = decoderContext.DecodeBool();
        transaction.Data = decoderContext.DecodeByteArray();
    }

    public static void EncodeDepositTxPayloadWithoutPayload(DepositTransaction item, RlpStream stream)
    {
        stream.Encode(item.SourceHash);
        stream.Encode(item.SenderAddress);
        stream.Encode(item.To);
        stream.Encode(item.Mint);
        stream.Encode(item.Value);
        stream.Encode(item.GasLimit);
        stream.Encode(item.IsOPSystemTransaction);
        stream.Encode(item.Data);
    }

    public static int GetDepositTxContentLength(DepositTransaction item)
    {
        return Rlp.LengthOf(item.SourceHash)
               + Rlp.LengthOf(item.SenderAddress)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Mint)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.IsOPSystemTransaction)
               + Rlp.LengthOf(item.Data);
    }
}
