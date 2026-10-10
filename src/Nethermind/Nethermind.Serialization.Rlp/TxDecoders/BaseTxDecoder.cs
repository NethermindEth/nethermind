// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract class BaseTxDecoder(TxType txType, Func<Transaction>? transactionFactory = null) : ITxDecoder
{
    private const int MaxDelayedHashTxnSize = 32768;
    private readonly Func<Transaction> _createTransaction = transactionFactory ?? (static () => new Transaction());

    // 30MB should be good enough for 300MGas block just filled with call data
    private static readonly RlpLimit _dataRlpLimit = RlpLimit.For<Transaction>((int)30.MiB, nameof(Transaction.Data));

    public TxType Type => txType;

    public abstract void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>Decodes a transaction sequence whose fields and trailing items <typeparamref name="TPayload"/> decodes.</summary>
    protected void DecodeTransaction<TPayload>(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors)
        where TPayload : struct, ITxPayloadDecoder
    {
        transaction ??= _createTransaction();
        transaction.Type = txType;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        // ReadSequenceLength does not check the declared length against the bytes on hand, so the payload
        // extent is the envelope this transaction was handed, not what its own header claims.
        TPayload.DecodePayload(transaction, ref decoderContext,
            Math.Min(lastCheck, txSequenceStart + transactionSequence.Length), rlpBehaviors);

        if (decoderContext.Position < lastCheck)
        {
            TPayload.DecodeTrailing(transaction, ref decoderContext, rlpBehaviors);
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

    protected static void CalculateHash(Transaction transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref RlpReader decoderContext)
    {
        if (transactionSequence.Length <= MaxDelayedHashTxnSize)
        {
            // Delay hash generation, as may be filtered as having too low gas etc
            if (decoderContext.IsMemoryBacked)
            {
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

    public abstract void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    public abstract int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);

    /// <summary>Decodes the <c>[nonce, gas_price, gas_limit, to, value, data]</c> fields, with <c>max_fee_per_gas</c>
    /// after the gas price when <paramref name="hasMaxFeePerGas"/> is set.</summary>
    protected static void DecodeLegacyFields(Transaction transaction, ref RlpReader decoderContext, bool hasMaxFeePerGas)
    {
        LiteRlpReader rlp = new(decoderContext.Data);
        decoderContext.Position = DecodeNonce(rlp, decoderContext.Position, out ulong nonce);
        transaction.Nonce = nonce;

        transaction.GasPrice = decoderContext.DecodeUInt256();
        if (hasMaxFeePerGas)
        {
            transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
        }

        int position = decoderContext.Position;
        rlp.DecodeULong(ref position, out ulong gasLimit);
        transaction.GasLimit = gasLimit;
        transaction.To = rlp.DecodeAddressOrNull(ref position);
        rlp.DecodeUInt256(ref position, out UInt256 value);
        transaction.Value = value;
        decoderContext.Position = position;

        // The Memory-returning byte-string decode is reader-only, so it takes the cursor back.
        transaction.Data = decoderContext.DecodeByteArrayMemory(_dataRlpLimit);
    }

    private static int DecodeNonce(LiteRlpReader rlp, int position, out ulong nonce)
    {
        (_, int contentLength) = rlp.PeekPrefixAndContentLength(position);
        if (contentLength <= sizeof(ulong))
        {
            rlp.DecodeULong(ref position, out nonce);
            return position;
        }

        int noncePosition = position;
        _ = RlpHelpers.DecodeByteArraySpan(rlp.Data, position, out ReadOnlySpan<byte> nonceBytes, RlpLimit.DefaultLimit);
        if (nonceBytes[0] == 0)
        {
            RlpHelpers.ThrowNonCanonicalInteger(noncePosition);
        }

        nonce = default;
        return RlpHelpers.ThrowNonceTooWide(noncePosition);
    }

    /// <summary>Reads the trailing <c>[v, r, s]</c> items.</summary>
    /// <exception cref="RlpException">The items are truncated.</exception>
    protected static ulong DecodeSignatureItems(ref RlpReader decoderContext, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes)
    {
        try
        {
            LiteRlpReader rlp = new(decoderContext.Data);
            int position = decoderContext.Position;
            rlp.DecodeULong(ref position, out ulong v);
            position = RlpHelpers.DecodeByteArraySpanUpTo32(rlp.Data, position, out rBytes);
            position = RlpHelpers.DecodeByteArraySpanUpTo32(rlp.Data, position, out sBytes);
            decoderContext.Position = position;
            return v;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new RlpException("RLP data is truncated: transaction signature is incomplete.", e);
        }
    }

    /// <summary>Decodes the trailing <c>[y_parity, r, s]</c> of a typed transaction, keeping its current signature when they hold none.</summary>
    protected static void DecodeTypedSignature(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors)
    {
        ulong v = DecodeSignatureItems(ref decoderContext, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    /// <summary>Writes the EIP-2718 prefix of a typed transaction whose encoding after the type byte is <paramref name="bodyLength"/> long.</summary>
    protected static void StartTypedTransaction<TWriter>(ref TWriter writer, TxType txType, int bodyLength, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            writer.StartByteArray(bodyLength + 1, false);
        }

        writer.WriteByte((byte)txType);
    }

    /// <summary>The length of a typed transaction whose encoding after the type byte is <paramref name="bodyLength"/> long.</summary>
    protected static int GetTypedTransactionLength(int bodyLength, RlpBehaviors rlpBehaviors) =>
        (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) != 0
            ? 1 + bodyLength
            : Rlp.LengthOfSequence(1 + bodyLength);

    /// <summary>The length of the <c>[nonce, gas_price, gas_limit, to, value, data]</c> fields a legacy transaction opens with.</summary>
    protected static int GetLegacyFieldsLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.Nonce)
        + Rlp.LengthOf(transaction.GasPrice)
        + Rlp.LengthOf(transaction.GasLimit)
        + Rlp.LengthOf(transaction.To)
        + Rlp.LengthOf(transaction.ValueRef)
        + Rlp.LengthOf(transaction.Data);

    protected static void EncodeLegacyFields<TWriter>(Transaction transaction, ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(transaction.Nonce);
        writer.Encode(transaction.GasPrice);
        writer.Encode(transaction.GasLimit);
        writer.Encode(transaction.To);
        writer.Encode(in transaction.ValueRef);
        writer.Encode(transaction.Data);
    }

    /// <summary>The length of the trailing <c>[v, r, s]</c>, where <paramref name="v"/> is the first element; a missing signature takes three empty items.</summary>
    protected static int GetSignatureLength(Signature? signature, ulong v) =>
        signature is null
            ? 3
            : Rlp.LengthOf(v)
              + Rlp.LengthOf(signature.RAsSpan.WithoutLeadingZeros())
              + Rlp.LengthOf(signature.SAsSpan.WithoutLeadingZeros());

    /// <summary>The length of the trailing <c>[y_parity, r, s]</c> of a typed transaction, or zero when encoding for signing.</summary>
    protected static int GetTypedSignatureLength(Signature? signature, bool forSigning) =>
        forSigning ? 0 : GetSignatureLength(signature, signature?.RecoveryId ?? 0);

    protected static void EncodeTypedSignature<TWriter>(Signature? signature, bool forSigning, ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (!forSigning)
        {
            EncodeSignature(signature, signature?.RecoveryId ?? 0, ref writer);
        }
    }

    protected static void EncodeSignature<TWriter>(Signature? signature, ulong v, ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (signature is null)
        {
            writer.Encode(0);
            writer.Encode(Bytes.Empty);
            writer.Encode(Bytes.Empty);
        }
        else
        {
            writer.Encode(v);
            writer.Encode(signature.RAsSpan.WithoutLeadingZeros());
            writer.Encode(signature.SAsSpan.WithoutLeadingZeros());
        }
    }
}
