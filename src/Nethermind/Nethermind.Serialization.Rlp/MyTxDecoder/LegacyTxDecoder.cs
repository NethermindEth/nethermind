// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.RlpWriter;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class LegacyTxDecoder(Func<Transaction>? transactionFactory = null) : ITxDecoder
{
    private readonly Func<Transaction> _createTransaction = transactionFactory ?? (() => new Transaction());

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Transaction transaction = _createTransaction();
        transaction.Type = TxType.Legacy;

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
        transaction ??= _createTransaction();
        transaction.Type = TxType.Legacy;

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

    private static void DecodePayload(Transaction transaction, RlpStream rlpStream)
    {
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
    }

    private static void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory();
    }

    private static void DecodeSignature(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void DecodeSignature(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    // NOTE: These methods could be lifted to the top class `TxDecoder`
    public void Encode(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        var writer = new RlpSequenceStreamWriter();
        WriteTransaction(writer, transaction, forSigning, isEip155Enabled, chainId);
        writer.WriteToStream(stream);
    }

    public int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        var writer = new RlpContentLengthWriter();
        WriteTransaction(writer, transaction, forSigning, isEip155Enabled, chainId);
        return Rlp.LengthOfSequence(writer.ContentLength);
    }

    public static void WriteTransaction(IRlpWriter writer, Transaction transaction, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        writer.Write(transaction.Nonce);
        writer.Write(transaction.GasPrice);
        writer.Write(transaction.GasLimit);
        writer.Write(transaction.To);
        writer.Write(transaction.Value);
        writer.Write(transaction.Data);

        if (forSigning)
        {
            bool includeSigChainIdHack = isEip155Enabled && chainId != 0;
            if (includeSigChainIdHack)
            {
                writer.Write(chainId);
                writer.Write(Rlp.OfEmptyByteArray);
                writer.Write(Rlp.OfEmptyByteArray);
            }
        }
        else
        {
            if (transaction.Signature is null)
            {
                writer.Write(0);
                writer.Write(Bytes.Empty);
                writer.Write(Bytes.Empty);
            }
            else
            {
                writer.Write(transaction.Signature.V);
                writer.Write(transaction.Signature.RAsSpan.WithoutLeadingZeros());
                writer.Write(transaction.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }
}
