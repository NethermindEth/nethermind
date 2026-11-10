// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract class BaseTxDecoder(TxType txType, Func<Transaction>? transactionFactory = null) : ITxDecoder
{
    private const int MaxDelayedHashTxnSize = 32768;
    private readonly Func<Transaction> _createTransaction = transactionFactory ?? (static () => new Transaction());

    // 30MB should be good enough for 300MGas block just filled with call data
    private const int DataLimit = 30 * 1024 * 1024;
    private static readonly RlpLimit _dataRlpLimit = RlpLimit.For<Transaction>(DataLimit, nameof(Transaction.Data));

    public TxType Type => txType;

    public abstract void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>Decodes a transaction sequence whose fields and trailing items <typeparamref name="TPayload"/> decodes.</summary>
    protected void DecodeTransaction<TPayload>(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TPayload : struct, ITxPayloadDecoder
    {
        if ((rlpBehaviors & RlpBehaviors.TrustedInput) != 0)
        {
            DecodeTransaction<TPayload, OffFlag>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
        }
        else
        {
            DecodeTransaction<TPayload, OnFlag>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
        }
    }

    // A root of its own: inlined into the dispatch, it would use up the inline budget that the reads need.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DecodeTransaction<TPayload, TCanonicalChecks>(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TPayload : struct, ITxPayloadDecoder
        where TCanonicalChecks : struct, IFlag
    {
        transaction ??= _createTransaction();
        transaction.Type = txType;

        RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        TPayload.DecodePayload<TCanonicalChecks>(transaction, ref fields, source, rlpBehaviors);

        if (fields.HasNext)
        {
            TPayload.DecodeTrailing<TCanonicalChecks>(transaction, ref fields, rlpBehaviors);
        }

        reader.ExitSequence(in fields, itemLength, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            CalculateHash(transaction, transactionSequence, source);
        }
    }

    /// <summary>Sets the hash of <paramref name="transaction"/>, or the bytes to compute it from later.</summary>
    /// <param name="source">The memory that holds <paramref name="transactionSequence"/>, or empty when the bytes must be copied.</param>
    protected static void CalculateHash(Transaction transaction, ReadOnlySpan<byte> transactionSequence, Memory<byte> source)
    {
        if (transactionSequence.Length <= MaxDelayedHashTxnSize)
        {
            // Delay hash generation, as may be filtered as having too low gas etc
            if (!source.IsEmpty)
            {
                transaction.SetPreHashMemoryNoLock(source.SliceOf(transactionSequence));
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

    public abstract void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);

    public abstract int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0);

    /// <summary>Decodes the <c>[nonce, gas_price, gas_limit, to, value, data]</c> fields, with <c>max_fee_per_gas</c>
    /// after the gas price when <paramref name="hasMaxFeePerGas"/> is set.</summary>
    /// <param name="source">The memory that holds the bytes of <paramref name="reader"/>; the data refers to it when it is not empty.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    protected static void DecodeLegacyFields<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, bool hasMaxFeePerGas)
        where TCanonicalChecks : struct, IFlag
    {
        transaction.Nonce = DecodeNonce<TCanonicalChecks>(ref reader);
        transaction.GasPrice = reader.DecodeUInt256<TCanonicalChecks>();
        if (hasMaxFeePerGas)
        {
            transaction.DecodedMaxFeePerGas = reader.DecodeUInt256<TCanonicalChecks>();
        }

        transaction.GasLimit = reader.DecodeULong<TCanonicalChecks>();
        transaction.To = reader.DecodeAddressOrNull();
        transaction.Value = reader.DecodeUInt256<TCanonicalChecks>();
        transaction.Data = DecodeData<TCanonicalChecks>(ref reader, source);
    }

    /// <summary>Reads the data as <see cref="RlpReaderExtensions.DecodeByteArrayMemory{TCanonicalChecks}"/> does under the data limit.</summary>
    /// <remarks>The limit check stays out of line, so that the hot path does not copy the limit.</remarks>
    private static ReadOnlyMemory<byte> DecodeData<TCanonicalChecks>(ref RlpReader reader, Memory<byte> source) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> data = reader.ReadBytes<TCanonicalChecks>();
        if (data.Length > DataLimit) ThrowOverLimit(data.Length, reader.Remaining, _dataRlpLimit);
        if (data.IsEmpty) return ReadOnlyMemory<byte>.Empty;
        if (!source.IsEmpty) return source.SliceOf(data);
        return data.Length == 1 && data[0] < Rlp.EmptyByteArrayByte ? RlpReaderExtensions.SingleByteArrays[data[0]] : data.ToArray();
    }

    /// <summary>Decodes a nonce, reporting one wider than 64 bits as <c>NonceTooWide</c> rather than as a generic
    /// integer-width error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static ulong DecodeNonce<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> bytes = reader.ReadIntegerBytes<TCanonicalChecks>();
        if (bytes.Length > sizeof(ulong)) ThrowNonceTooWide();
        return RlpReaderExtensions.Word(bytes, 0, bytes.Length);
    }

    // Ethereum tests match the NonceTooWide prefix for NONCE_OVERFLOW.
    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNonceTooWide() => throw new RlpReaderException("NonceTooWide: Transaction nonce exceeds uint64");

    /// <summary>Reads a sequence of 32-byte versioned hashes.</summary>
    /// <remarks>
    /// Every item is 33 bytes, so the count comes from the length of the sequence and the items are not counted first.
    /// Bytes that do not make whole items fail the read of the last item or the end check.
    /// </remarks>
    protected static byte[][] DecodeVersionedHashes<TCanonicalChecks>(ref RlpReader reader, RlpLimit limit) where TCanonicalChecks : struct, IFlag
    {
        const int itemLength = 1 + Hash256.Size;
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int sequenceLength);
        int count = items.Remaining / itemLength;
        if (count > limit.Limit) ThrowOverLimit(count, items.Remaining, limit);

        if (count == 0)
        {
            reader.ExitSequence(in items, sequenceLength);
            return [];
        }

        // A plain new array keeps its exact type, so the stores below need no covariance check.
        byte[][] hashes = new byte[count][];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = items.ReadBytes<TCanonicalChecks>(Hash256.Size).ToArray();
        }

        reader.ExitSequence(in items, sequenceLength);
        return hashes;
    }

    /// <summary>Reads the trailing <c>[v, r, s]</c> items.</summary>
    /// <exception cref="RlpReaderException">The items are truncated or not canonical.</exception>
    protected static ulong DecodeSignatureItems<TCanonicalChecks>(ref RlpReader reader, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes)
        where TCanonicalChecks : struct, IFlag
    {
        ulong v = reader.DecodeULong<TCanonicalChecks>();
        rBytes = DecodeSignatureScalar<TCanonicalChecks>(ref reader);
        sBytes = DecodeSignatureScalar<TCanonicalChecks>(ref reader);
        return v;
    }

    /// <summary>Reads a byte string of at most 32 bytes, as <see cref="RlpReaderExtensions.DecodeByteArraySpan(ref RlpReader, RlpLimit?, int)"/> does under <see cref="RlpLimit.L32"/>.</summary>
    /// <remarks>The limit check stays out of line, so that the hot path does not copy the limit.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> DecodeSignatureScalar<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> value = reader.ReadBytes<TCanonicalChecks>();
        if (value.Length > Hash256.Size) ThrowOverLimit(value.Length, reader.Remaining, RlpLimit.L32);
        return value;
    }

    [DoesNotReturn, StackTraceHidden, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOverLimit(int length, int remaining, RlpLimit limit) => Rlp.ThrowCountOverLimit((uint)length, remaining + length, limit);

    /// <summary>Decodes the trailing <c>[y_parity, r, s]</c> of a typed transaction, keeping its current signature when they hold none.</summary>
    protected static void DecodeTypedSignature<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        ulong v = DecodeSignatureItems<TCanonicalChecks>(ref reader, out ReadOnlySpan<byte> rBytes, out ReadOnlySpan<byte> sBytes);
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    /// <summary>Writes the EIP-2718 prefix of a typed transaction whose encoding after the type byte is <paramref name="bodyLength"/> long.</summary>
    protected static void StartTypedTransaction(ref RlpWriter writer, TxType txType, int bodyLength, RlpBehaviors rlpBehaviors)
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

    protected static void EncodeLegacyFields(Transaction transaction, ref RlpWriter writer)
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

    protected static void EncodeTypedSignature(Signature? signature, bool forSigning, ref RlpWriter writer)
    {
        if (!forSigning)
        {
            EncodeSignature(signature, signature?.RecoveryId ?? 0, ref writer);
        }
    }

    protected static void EncodeSignature(Signature? signature, ulong v, ref RlpWriter writer)
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
