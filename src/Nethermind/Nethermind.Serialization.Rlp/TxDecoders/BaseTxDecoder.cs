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

    // 30MB should be good enough for 300MGas block just filled with call data
    private static readonly RlpLimit _dataRlpLimit = RlpLimit.For<Transaction>((int)30.MiB, nameof(Transaction.Data));

    public TxType Type => txType;

    public virtual void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
            CalculateHash(transaction, transactionSequence);
        }
    }

    protected static void CalculateHash(Transaction transaction, ReadOnlySpan<byte> transactionSequence)
    {
        if (transactionSequence.Length <= MaxDelayedHashTxnSize)
        {
            // Delay hash generation, as may be filtered as having too low gas etc
            transaction.SetPreHashNoLock(transactionSequence);
        }
        else
        {
            // Just calculate the Hash immediately as txn too large
            transaction.Hash = Keccak.Compute(transactionSequence);
        }
    }

    public virtual void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);

        writer.StartSequence(contentLength);
        EncodePayload(transaction, ref writer);
        EncodeSignature(transaction.Signature, ref writer, forSigning, isEip155Enabled, chainId);
    }

    public virtual int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);
        return txPayloadLength;
    }

    protected virtual void DecodePayload(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction.Nonce = decoderContext.DecodeUInt256();
        DecodeGasPrice(transaction, ref decoderContext);
        transaction.GasLimit = decoderContext.DecodePositiveLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArray(_dataRlpLimit);
    }

    protected virtual void DecodeGasPrice(Transaction transaction, ref RlpReader decoderContext) => transaction.GasPrice = decoderContext.DecodeUInt256();

    protected Signature? DecodeSignature(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan(RlpLimit.L32);
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan(RlpLimit.L32);
        return DecodeSignature(v, rBytes, sBytes, transaction.Signature, rlpBehaviors);
    }

    protected virtual Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? fallbackSignature;

    protected virtual void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(transaction.Nonce);
        EncodeGasPrice(transaction, ref writer);
        writer.Encode(transaction.GasLimit);
        writer.Encode(transaction.To);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.Data);
    }

    protected virtual void EncodeGasPrice<TWriter>(Transaction transaction, ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.Encode(transaction.GasPrice);

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

    protected virtual void EncodeSignature<TWriter>(Signature? signature, ref TWriter writer, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (!forSigning)
        {
            if (signature is null)
            {
                writer.Encode(0);
                writer.Encode(Bytes.Empty);
                writer.Encode(Bytes.Empty);
            }
            else
            {
                writer.Encode(GetSignatureFirstElement(signature));
                writer.Encode(signature.RAsSpan.WithoutLeadingZeros());
                writer.Encode(signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }
}
