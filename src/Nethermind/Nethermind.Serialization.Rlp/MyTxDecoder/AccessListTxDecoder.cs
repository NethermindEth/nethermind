// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;
using Nethermind.Serialization.Rlp.RlpWriter;
using Nethermind.Serialization.Rlp.RlpReader;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class AccessListTxDecoder(Func<Transaction>? transactionFactory = null) : ITxDecoder
{
    private static readonly AccessListDecoder AccessListDecoder = new();
    private readonly Func<Transaction> _createTransaction = transactionFactory ?? (() => new Transaction());

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = _createTransaction();
        transaction.Type = TxType.AccessList;

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayload(transaction, rlpStream, rlpBehaviors);

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
        transaction ??= _createTransaction();
        transaction.Type = TxType.AccessList;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayload(transaction, ref decoderContext, rlpBehaviors);

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

    private static void DecodePayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = rlpStream.DecodeULong();
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
        transaction.AccessList = AccessListDecoder.Decode(rlpStream, rlpBehaviors);
    }

    private static void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory();
        transaction.AccessList = AccessListDecoder.Decode(ref decoderContext, rlpBehaviors);
    }

    private static void DecodeSignature(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void DecodeSignature(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void WriteTransaction(IRlpWriter writer, Transaction transaction, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false)
    {
        writer.Wrap(when: !rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping), bytes: 1, writer =>
        {
            writer.WriteByte((byte)TxType.AccessList);

            writer.WriteSequence(writer =>
            {
                WritePayload(writer, transaction, rlpBehaviors);
                WriteSignature(writer, transaction, forSigning);
            });
        });
    }

    private static void WritePayload(IRlpWriter writer, Transaction transaction, RlpBehaviors rlpBehaviors)
    {
        writer.Write(transaction.ChainId ?? 0);
        writer.Write(transaction.Nonce);
        writer.Write(transaction.GasPrice);
        writer.Write(transaction.GasLimit);
        writer.Write(transaction.To);
        writer.Write(transaction.Value);
        writer.Write(transaction.Data);
        writer.Write(AccessListDecoder.Instance, transaction.AccessList, rlpBehaviors);
    }

    private static void WriteSignature(IRlpWriter writer, Transaction transaction, bool forSigning)
    {
        if (!forSigning)
        {
            if (transaction.Signature is null)
            {
                writer.Write(0);
                writer.Write(Bytes.Empty);
                writer.Write(Bytes.Empty);
            }
            else
            {
                writer.Write(transaction.Signature.RecoveryId);
                writer.Write(transaction.Signature.RAsSpan.WithoutLeadingZeros());
                writer.Write(transaction.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }

    // NOTE: This should replace the top `TxDecoder.Decode` method.
    // An open question is how to deal with invalid TxType (chain of responsability, catch and continue, etc...)
    public Transaction? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        Transaction transaction = _createTransaction();
        transaction.Type = TxType.AccessList;

        var reader = new RlpStreamReader(rlpStream);
        var sequence = ReadTypedTransaction(reader, rlpBehaviors, reader => {
            ReadTransaction(reader, transaction, rlpBehaviors);
        });

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (sequence.Length <= ITxDecoder.MaxDelayedHashTxnSize)
            {
                transaction.SetPreHashNoLock(sequence);
            }
            else
            {
                transaction.Hash = Keccak.Compute(sequence);
            }
        }

        return transaction;
    }

    private static void ReadTransaction(RlpStreamReader reader, Transaction transaction, RlpBehaviors rlpBehaviors)
    {
        var txType = reader.ReadByte();
        if (txType != (byte)TxType.AccessList)
        {
            throw new RlpException("Invalid transaction type");
        }

        reader.ReadSequence(reader =>
        {
            ReadPayload(reader, transaction, rlpBehaviors);
            if (reader.HasRemainder)
            {
                ReadSignature(reader, transaction, rlpBehaviors);
            }
        });
    }

    private static void ReadPayload(RlpStreamReader reader, Transaction transaction, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = reader.ReadULong();
        transaction.Nonce = reader.ReadUInt256();
        transaction.GasPrice = reader.ReadUInt256();
        transaction.GasLimit = reader.ReadLong();
        transaction.To = reader.ReadAddress();
        transaction.Value = reader.ReadUInt256();
        transaction.Data = reader.ReadByteArray();
        transaction.AccessList = reader.Read(AccessListDecoder.Instance, rlpBehaviors);
    }

    private static void ReadSignature(RlpStreamReader reader, Transaction transaction, RlpBehaviors rlpBehaviors)
    {
        ulong v = reader.ReadULong();
        ReadOnlySpan<byte> rBytes = reader.ReadByteArraySpan();
        ReadOnlySpan<byte> sBytes = reader.ReadByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    // NOTE: This can be lifted to a common utility class/extension methid
    private static ReadOnlySpan<byte> ReadTypedTransaction(RlpStreamReader reader, RlpBehaviors rlpBehaviors, Action<RlpStreamReader> block)
    {
        var strict = (rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0;
        ReadOnlySpan<byte> sequence = rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping)
            ? reader.ReadUntilEnd(strict, block)
            : reader.ReadSequence(strict, block);
        return sequence;
    }
}
