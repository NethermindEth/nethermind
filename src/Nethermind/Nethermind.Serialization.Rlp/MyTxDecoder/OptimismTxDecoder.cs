// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp.RlpWriter;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class OptimismTxDecoder(Func<Transaction>? transactionFactory = null) : ITxDecoder
{
    private readonly Func<Transaction> _createTransaction = transactionFactory ?? (() => new Transaction());

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = _createTransaction();
        transaction.Type = TxType.DepositTx;

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayload(transaction, rlpStream);

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(transaction, rlpStream, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            rlpStream.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (transactionSequence.Length <= ITxDecoder.MaxDelayedHashTxnSize)
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

    public void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction ??= new();
        transaction.Type = TxType.DepositTx;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayload(transaction, ref decoderContext);

        if (decoderContext.Position < lastCheck)
        {
            DecodeSignature(transaction, ref decoderContext, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (transactionSequence.Length <= ITxDecoder.MaxDelayedHashTxnSize)
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

    public void Encode(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        var writer = new RlpStreamWriter(stream);
        WriteTransaction(writer, transaction, rlpBehaviors, forSigning);
    }

    public int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        var writer = new RlpContentLengthWriter();
        WriteTransaction(writer, transaction, rlpBehaviors, forSigning);
        return writer.ContentLength;
    }

    private static void DecodePayload(Transaction transaction, RlpStream rlpStream)
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

    private static void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
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

    private static void DecodeSignature(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void DecodeSignature(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void WriteTransaction(IRlpWriter writer, Transaction transaction, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false)
    {
        writer.Wrap(when: !rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping), bytes: 1, writer =>
        {
            writer.WriteByte((byte)TxType.DepositTx);

            writer.WriteSequence(writer =>
            {
                WritePayload(writer, transaction, rlpBehaviors);
            });
        });
    }

    private static void WritePayload(IRlpWriter writer, Transaction transaction, RlpBehaviors rlpBehaviors)
    {
        writer.Write(transaction.SourceHash);
        writer.Write(transaction.SenderAddress);
        writer.Write(transaction.To);
        writer.Write(transaction.Mint);
        writer.Write(transaction.Value);
        writer.Write(transaction.GasLimit);
        writer.Write(transaction.IsOPSystemTransaction);
        writer.Write(transaction.Data);
    }
}
