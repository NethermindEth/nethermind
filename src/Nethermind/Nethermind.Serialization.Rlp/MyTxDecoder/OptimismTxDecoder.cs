// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class OptimismTxDecoder(bool lazyHash = true) : AbstractTxDecoder
{
    public override Transaction Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.DepositTx
        };

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayloadWithoutSig(transaction, rlpStream);

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

    public override Transaction Decode(int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.DepositTx
        };

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayloadWithoutSig(transaction, ref decoderContext);

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

        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors);
    }

    private static void DecodeSignature(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors, Transaction transaction)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();

        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors);
    }

    public override Rlp EncodeTx(Transaction? item, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item?.Type != TxType.DepositTx) { throw new InvalidOperationException("Unexpected TxType"); }

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(item, rlpStream, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item?.Type != TxType.DepositTx) { throw new InvalidOperationException("Unexpected TxType"); }

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
        EncodePayloadWithoutSignature(item, stream);
    }

    public override int GetLength(Transaction tx, RlpBehaviors rlpBehaviors)
    {
        if (tx?.Type != TxType.DepositTx) { throw new InvalidOperationException("Unexpected TxType"); }

        int txContentLength = GetContentLength(tx);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength); // Rlp(TransactionType || TransactionPayload)
        return result;
    }

    private static void DecodePayloadWithoutSig(Transaction transaction, RlpStream rlpStream)
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

    private static void DecodePayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
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

    private static void EncodePayloadWithoutSignature(Transaction item, RlpStream stream)
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

    private static int GetContentLength(Transaction item)
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
