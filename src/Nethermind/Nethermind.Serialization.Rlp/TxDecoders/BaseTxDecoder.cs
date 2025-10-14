// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract class BaseTxDecoder<T>(TxType txType, Func<T>? transactionFactory = null)
    : ITxDecoder where T : Transaction, new()
{
    private const int MaxDelayedHashTxnSize = 32768;
    private readonly Func<T> _createTransaction = transactionFactory ?? (static () => new T());

    public TxType Type => txType;

    public virtual Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T transaction = _createTransaction();
        transaction.Type = txType;

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayload(transaction, rlpStream, rlpBehaviors);

        if (rlpStream.Position < lastCheck)
        {
            transaction.Signature = DecodeSignature(transaction, rlpStream, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            rlpStream.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            CalculateHash(transaction, transactionSequence);
        }

        return transaction;
    }

    protected void CalculateHash(Transaction transaction, ReadOnlySpan<byte> transactionSequence)
    {
        if (transactionSequence.Length <= MaxDelayedHashTxnSize)
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

    public virtual void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction ??= _createTransaction();
        transaction.Type = txType;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayload(transaction, ref decoderContext, rlpBehaviors);

        if (decoderContext.Position < lastCheck)
        {
            transaction.Signature = DecodeSignature(transaction, ref decoderContext, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            CalculateHash(transaction, txSequenceStart, transactionSequence, ref decoderContext);
        }
    }

    protected void CalculateHash(Transaction transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext)
    {
        if (transactionSequence.Length <= MaxDelayedHashTxnSize)
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

    public virtual void Encode(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);

        stream.StartSequence(contentLength);
        EncodePayload(transaction, stream);
        EncodeSignature(transaction.Signature, stream, forSigning, isEip155Enabled, chainId);
    }

    public virtual int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);
        return txPayloadLength;
    }

    protected virtual void DecodePayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction.Nonce = rlpStream.DecodeUInt256();
        DecodeGasPrice(transaction, rlpStream);
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
    }

    protected virtual void DecodeGasPrice(Transaction transaction, RlpStream rlpStream)
    {
        transaction.GasPrice = rlpStream.DecodeUInt256();
    }

    protected virtual void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction.Nonce = decoderContext.DecodeUInt256();
        DecodeGasPrice(transaction, ref decoderContext);
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory();
    }

    protected virtual void DecodeGasPrice(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        transaction.GasPrice = decoderContext.DecodeUInt256();
    }

    protected Signature? DecodeSignature(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan(RlpLimit.L32);
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan(RlpLimit.L32);
        return DecodeSignature(v, rBytes, sBytes, transaction.Signature, rlpBehaviors);
    }

    protected Signature? DecodeSignature(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan(RlpLimit.L32);
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan(RlpLimit.L32);
        return DecodeSignature(v, rBytes, sBytes, transaction.Signature, rlpBehaviors);
    }

    protected virtual Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? fallbackSignature;

    protected virtual void EncodePayload(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(transaction.Nonce);
        EncodeGasPrice(transaction, stream);
        stream.Encode(transaction.GasLimit);
        stream.Encode(transaction.To);
        stream.Encode(in transaction.ValueRef);
        stream.Encode(transaction.Data);
    }

    protected virtual void EncodeGasPrice(Transaction transaction, RlpStream stream)
    {
        stream.Encode(transaction.GasPrice);
    }

    protected virtual int GetContentLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetPayloadLength(transaction) + GetSignatureLength(transaction.Signature, forSigning, isEip155Enabled, chainId);

    protected virtual int GetPayloadLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.Nonce)
        + Rlp.LengthOf(transaction.GasPrice)
        + Rlp.LengthOf(transaction.GasLimit)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(in transaction.ValueRef)
        + Rlp.LengthOf(transaction.Data);

    protected virtual int GetSignatureLength(Signature? signature, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = 0;

        if (!forSigning)
        {
            if (signature is null)
            {
                contentLength += 1;
                contentLength += 1;
                contentLength += 1;
            }
            else
            {
                contentLength += Rlp.LengthOf(GetSignatureFirstElement(signature));
                contentLength += Rlp.LengthOf(signature.RAsSpan.WithoutLeadingZeros());
                contentLength += Rlp.LengthOf(signature.SAsSpan.WithoutLeadingZeros());
            }
        }

        return contentLength;
    }

    protected virtual ulong GetSignatureFirstElement(Signature signature) => signature.RecoveryId;

    protected virtual void EncodeSignature(Signature? signature, RlpStream stream, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
        if (!forSigning)
        {
            if (signature is null)
            {
                stream.Encode(0);
                stream.Encode(Bytes.Empty);
                stream.Encode(Bytes.Empty);
            }
            else
            {
                stream.Encode(GetSignatureFirstElement(signature));
                stream.Encode(signature.RAsSpan.WithoutLeadingZeros());
                stream.Encode(signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }
}
