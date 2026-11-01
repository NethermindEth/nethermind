// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>Decodes the EIP-8141 frame transaction payload <c>[chain_id, nonce, sender, frames, signatures, fees,
/// blob_versioned_hashes]</c>, where <c>fees = [max_fee, max_priority_fee_per_gas]</c>,
/// with EIP-8250's <c>nonce_keys, nonce_seq</c> in place of <c>nonce</c>.</summary>
/// <remarks>The sender is explicit, so there is no envelope signature or recovery. The wrapper and plain forms are
/// disjoint: a wrapper opens with a list, a plain payload with the <c>chain_id</c> scalar.</remarks>
public sealed class FrameTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(TxType.FrameTx, transactionFactory)
{
    private static readonly RlpLimit FramesCountLimit = RlpLimit.For<Transaction>(Eip8141Constants.MaxFrames, nameof(Transaction.Frames));

    // Every entry is a four-item sequence, so five bytes at least.
    private const int MinSignatureRlpLength = 5;

    // The spec bounds the signature list only through gas: each entry is charged at least the ARBITRARY
    // verification price. The array is sized from the declared count, so the bytes on hand bound it too.
    private static RlpLimit SignaturesCountLimit(int bytesLeft) => RlpLimit.For<Transaction>(
        (int)Math.Min(RlpLimit.MaxBlockGas / Eip8141Constants.ArbitraryVerificationGasCost + 1,
            (ulong)bytesLeft / MinSignatureRlpLength),
        nameof(Transaction.FrameSignatures));

    // Decode-side allocation guard only — EIP-7594's per-tx blob limit is far tighter and is
    // enforced by the transaction validator.
    private static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(ShardBlobNetworkWrapperRlp.BlobCountLimit, nameof(Transaction.BlobVersionedHashes));

    public override void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) != 0)
        {
            bool isWrapper = (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
                ? DecodeIfNetworkWrapper<OffFlag>(ref transaction, ref reader, source, rlpBehaviors)
                : DecodeIfNetworkWrapper<OnFlag>(ref transaction, ref reader, source, rlpBehaviors);
            if (isWrapper) return;

            DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
            // EIP-7594: as for type-3, a blob-carrying transaction's mempool form is the sidecar wrapper.
            if (transaction is { CarriesBlobs: true }) ThrowMissingSidecar();
            return;
        }

        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowShortFeeList() =>
        throw new RlpReaderException($"{nameof(TxType.FrameTx)} fees must hold two items");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingSidecar() =>
        throw new RlpReaderException($"Blob-carrying {nameof(TxType.FrameTx)} in mempool form must carry a {nameof(ShardBlobNetworkWrapper)}");

    private bool DecodeIfNetworkWrapper<TCanonicalChecks>(ref Transaction? transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader wrapper = reader.EnterSequence<TCanonicalChecks>(out int wrapperLength);
        if (!wrapper.HasNext || !wrapper.IsNextSequence) return false;

        RlpReader peek = wrapper;
        ReadOnlySpan<byte> transactionSequence = peek.ReadEncodedItem<TCanonicalChecks>();

        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref wrapper, source,
            (rlpBehaviors | RlpBehaviors.ExcludeHashes) & ~RlpBehaviors.InMempoolForm);

        transaction!.NetworkWrapper = ShardBlobNetworkWrapperRlp.Decode<TCanonicalChecks>(ref wrapper, rlpBehaviors);
        reader.ExitSequence(in wrapper, wrapperLength, rlpBehaviors);

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            transaction.Hash = NetworkPayloadFormHash.Calculate(TxType.FrameTx, transactionSequence);
        }

        return true;
    }

    private static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source,
        RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        // The frames and signatures never took the other behaviors.
        RlpBehaviors itemBehaviors = rlpBehaviors & RlpBehaviors.TrustedInput;

        // EIP8141-DEVIATION: the spec allows chain_id < 2^256; decoded as u64, the codebase-wide ChainId width.
        transaction.ChainId = reader.DecodeULong<TCanonicalChecks>();
        transaction.NonceKeys = reader.IsNextSequence ? FrameTxNonceCalldata.DecodeKeys<TCanonicalChecks>(ref reader) : null;
        transaction.Nonce = reader.DecodeULong<TCanonicalChecks>();
        transaction.SenderAddress = reader.DecodeAddress();
        transaction.Frames = DecodeFrames<TCanonicalChecks>(ref reader, source, itemBehaviors);
        transaction.FrameSignatures = DecodeSignatures<TCanonicalChecks>(ref reader, source, itemBehaviors);
        RlpReader fees = reader.EnterSequence<TCanonicalChecks>(out int feesLength);
        transaction.MaxFee = fees.DecodeUInt256<TCanonicalChecks>();
        if (!fees.HasNext) ThrowShortFeeList();
        transaction.GasPrice = fees.DecodeUInt256<TCanonicalChecks>(); // max_priority_fee_per_gas
        transaction.DecodedMaxFeePerGas = FrameTxValidation.ImpliedMaxFeePerGas(transaction.MaxFee, FrameTxValidation.TotalGasLimit(transaction.Frames));
        transaction.MaxFeePerBlobGas = null;
        reader.ExitSequence(in fees, feesLength);
        transaction.BlobVersionedHashes = DecodeVersionedHashes<TCanonicalChecks>(ref reader, BlobVersionedHashesCountLimit);

        // A frame transaction has no gas_limit field; GasLimit carries the sum of frame gas limits so pre-execution
        // consumers reading it do not see ~0 gas. The processor derives the real tx_gas_limit.
        transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);

        if (transaction.NonceKeys is not null)
        {
            transaction.FrameCalldataStats = FrameTxNonceCalldata.Measure(transaction);
        }
    }

    private static TxFrame[] DecodeFrames<TCanonicalChecks>(ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        TxFrame[] frames = new TxFrame[items.CountItems<TCanonicalChecks>(FramesCountLimit)];
        for (int i = 0; i < frames.Length; i++)
        {
            if (items.IsNextItemEmptyList()) ThrowNullArrayElement(i);
            frames[i] = TxFrameDecoder.Decode<TCanonicalChecks>(ref items, source, rlpBehaviors);
        }

        reader.ExitSequence(in items, itemLength);
        return frames;
    }

    private static TxFrameSignature[] DecodeSignatures<TCanonicalChecks>(ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        int bytesLeft = reader.Remaining;
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        TxFrameSignature[] signatures = new TxFrameSignature[items.CountItems<TCanonicalChecks>(SignaturesCountLimit(bytesLeft))];
        for (int i = 0; i < signatures.Length; i++)
        {
            if (items.IsNextItemEmptyList()) ThrowNullArrayElement(i);
            signatures[i] = TxFrameSignatureDecoder.Decode<TCanonicalChecks>(ref items, source, rlpBehaviors);
        }

        reader.ExitSequence(in items, itemLength);
        return signatures;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNullArrayElement(int index) =>
        throw new RlpReaderException($"Unexpected null array element at index {index}");

    public override void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors, forSigning);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors, forSigning);

    internal static void EncodeTransaction(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int payloadContentLength = GetContentLength(transaction, forSigning);
        int payloadSequenceLength = Rlp.LengthOfSequence(payloadContentLength);

        // A sidecar-less blob carrier still serialises to the plain form that Decode refuses; see GetLength.
        ShardBlobNetworkWrapper? wrapper = rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
            ? transaction.NetworkWrapper as ShardBlobNetworkWrapper
            : null;

        int wrapperContentLength = wrapper is null ? 0 : payloadSequenceLength + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper, rlpBehaviors);
        int bodyLength = wrapper is null ? payloadSequenceLength : Rlp.LengthOfSequence(wrapperContentLength);

        StartTypedTransaction(ref writer, TxType.FrameTx, bodyLength, rlpBehaviors);

        if (wrapper is null)
        {
            RlpSequence sequence = writer.StartSequence(payloadContentLength);
            EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: forSigning);
            writer.EndSequence(sequence);
        }
        else
        {
            RlpSequence wrapperSequence = writer.StartSequence(wrapperContentLength);
            RlpSequence sequence = writer.StartSequence(payloadContentLength);
            // Elide on forSigning like payloadContentLength, or the declared length and the bytes drift.
            EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: forSigning);
            writer.EndSequence(sequence);
            ShardBlobNetworkWrapperRlp.Encode(ref writer, wrapper, rlpBehaviors);
            writer.EndSequence(wrapperSequence);
        }
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int payloadSequenceLength = Rlp.LengthOfSequence(GetContentLength(transaction, forSigning));

        // GetLength is the pool's sizing call, so it must stay total and Encode must agree with it.
        ShardBlobNetworkWrapper? wrapper = rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
            ? transaction.NetworkWrapper as ShardBlobNetworkWrapper
            : null;

        int bodyLength = wrapper is null
            ? payloadSequenceLength
            : Rlp.LengthOfSequence(payloadSequenceLength + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper, rlpBehaviors));

        return GetTypedTransactionLength(bodyLength, rlpBehaviors);
    }

    private static void EncodePayload(Transaction transaction, ref RlpWriter writer, bool elideCanonicalSignatureBytes)
    {
        writer.Encode(transaction.ChainId ?? 0);
        FrameTxNonceCalldata.Encode(transaction, ref writer);
        writer.Encode(transaction.SenderAddress);
        TxFrameDecoder.Instance.EncodeArray(ref writer, transaction.Frames);
        TxFrameSignatureDecoder.Instance.EncodeArray(ref writer, transaction.FrameSignatures, elideCanonicalSignatureBytes);
        RlpSequence fees = writer.StartSequence(GetFeesContentLength(transaction));
        writer.Encode(transaction.MaxFee);
        writer.Encode(transaction.GasPrice);
        writer.EndSequence(fees);
        EncodeVersionedHashes(ref writer, transaction.BlobVersionedHashes);
    }

    private static void EncodeVersionedHashes(ref RlpWriter writer, byte[]?[]? blobVersionedHashes)
    {
        RlpSequence sequence = writer.StartSequence(GetVersionedHashesContentLength(blobVersionedHashes));
        if (blobVersionedHashes is not null)
        {
            for (int i = 0; i < blobVersionedHashes.Length; i++)
            {
                writer.Encode(blobVersionedHashes[i]);
            }
        }

        writer.EndSequence(sequence);
    }

    private static int GetVersionedHashesLength(byte[]?[]? blobVersionedHashes) =>
        Rlp.LengthOfSequence(GetVersionedHashesContentLength(blobVersionedHashes));

    private static int GetVersionedHashesContentLength(byte[]?[]? blobVersionedHashes)
    {
        if (blobVersionedHashes is null)
        {
            return 0;
        }

        int contentLength = 0;
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            contentLength += Rlp.LengthOf(blobVersionedHashes[i] ?? throw new RlpReaderException($"{nameof(Transaction.BlobVersionedHashes)} contains a null versioned hash."));
        }

        return contentLength;
    }

    private static int GetFeesContentLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.MaxFee) + Rlp.LengthOf(transaction.GasPrice);

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + FrameTxNonceCalldata.Length(transaction)
        + Rlp.LengthOf(transaction.SenderAddress)
        + TxFrameDecoder.Instance.GetArrayLength(transaction.Frames)
        + TxFrameSignatureDecoder.Instance.GetArrayLength(transaction.FrameSignatures, elideCanonicalSignatureBytes: forSigning)
        + Rlp.LengthOfSequence(GetFeesContentLength(transaction))
        + GetVersionedHashesLength(transaction.BlobVersionedHashes);

    private readonly struct Payload : ITxPayloadDecoder
    {
        static void ITxPayloadDecoder.DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors) =>
            FrameTxDecoder.DecodePayload<TCanonicalChecks>(transaction, ref reader, source, rlpBehaviors);

        /// <remarks>A frame transaction carries no envelope signature and no element after its blob versioned hashes.</remarks>
        static void ITxPayloadDecoder.DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors) =>
            ThrowTrailingElement();
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTrailingElement() => throw new RlpReaderException("frame transaction must not carry a trailing element");
}

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see>'s <c>nonce_calldata</c> —
/// <c>rlp(nonce_keys) || rlp(nonce_seq)</c>, the key sequence being absent on a plain-nonce transaction.
/// </summary>
/// <remarks>Shared by the payload encoder and the intrinsic-gas path, so the charge is priced off exactly the bytes
/// the wire form carries.</remarks>
public static class FrameTxNonceCalldata
{
    private static readonly RlpLimit KeysCountLimit = RlpLimit.For<Transaction>(Eip8250Constants.MaxNonceKeys, nameof(Transaction.NonceKeys));

    public static void Encode(Transaction transaction, ref RlpWriter writer)
    {
        if (transaction.NonceKeys is { } nonceKeys)
        {
            EncodeKeys(nonceKeys, ref writer);
        }

        writer.Encode(transaction.Nonce);
    }

    /// <summary>Writes <c>nonce_keys</c> as a list of integers.</summary>
    public static void EncodeKeys(UInt256[] nonceKeys, ref RlpWriter writer)
    {
        RlpSequence sequence = writer.StartSequence(KeysContentLength(nonceKeys));
        foreach (UInt256 nonceKey in nonceKeys)
        {
            writer.Encode(nonceKey);
        }

        writer.EndSequence(sequence);
    }

    /// <summary>Reads <c>nonce_keys</c> as a list of integers.</summary>
    /// <remarks>Not <c>DecodeArray</c>: it substitutes the default for an empty-list element, turning the wire bytes
    /// <c>c1 c0</c> into the key set <c>[0]</c> instead of rejecting them.</remarks>
    public static UInt256[] DecodeKeys(ref RlpReader reader) => DecodeKeys<OnFlag>(ref reader);

    /// <inheritdoc cref="DecodeKeys(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [SkipLocalsInit]
    public static UInt256[] DecodeKeys<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        Span<UInt256> buffer = stackalloc UInt256[Eip8250Constants.MaxNonceKeys];
        int count = 0;
        while (items.HasNext)
        {
            if (count == buffer.Length)
            {
                Rlp.GuardLimit(count + 1, items.Remaining + count + 1, KeysCountLimit);
            }

            buffer[count++] = items.DecodeUInt256<TCanonicalChecks>();
        }

        // An element may not overrun the list's declared content length: without this the under-declared
        // header c1 82 01 2c decodes as [300] just like canonical c3 82 01 2c — two hashes, one signature.
        reader.ExitSequence(in items, itemLength);

        return buffer[..count].ToArray();
    }

    /// <summary>Length in bytes of what <see cref="Encode"/> writes for <paramref name="transaction"/>.</summary>
    public static int Length(Transaction transaction) =>
        (transaction.NonceKeys is { } nonceKeys ? KeysLength(nonceKeys) : 0)
        + Rlp.LengthOf(transaction.Nonce);

    /// <summary>Length in bytes of what <see cref="EncodeKeys"/> writes for <paramref name="nonceKeys"/>.</summary>
    public static int KeysLength(UInt256[] nonceKeys) => Rlp.LengthOfSequence(KeysContentLength(nonceKeys));

    private static int KeysContentLength(UInt256[] nonceKeys)
    {
        int contentLength = 0;
        foreach (UInt256 nonceKey in nonceKeys)
        {
            contentLength += Rlp.LengthOf(nonceKey);
        }

        return contentLength;
    }

    /// <summary>Zero and non-zero byte counts of the priced part of what <see cref="Encode"/> writes,
    /// measured off the encoded bytes so the EIP-8141 calldata charge cannot drift from the wire form.</summary>
    /// <remarks>A non-empty key set with <see cref="Eip8250Constants.MaxNonceSeq"/> prices only the keys: the
    /// single-use sequence is encoded and signed but exempt from data pricing.</remarks>
    public static (int ZeroBytes, int NonZeroBytes) Measure(Transaction transaction)
    {
        bool isSequencePriced = transaction.NonceKeys is not { Length: > 0 } || transaction.Nonce != Eip8250Constants.MaxNonceSeq;
        int length = isSequencePriced ? Length(transaction) : KeysLength(transaction.NonceKeys!);
        // A dynamic stackalloc turns an out-of-range length into an uncatchable stack overflow, and this is
        // public: the keys can reach it from an RPC-built transaction the decoder never bounded.
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxCalldataLength);
        Span<byte> calldata = stackalloc byte[length];
        RlpWriter writer = RlpWriter.ContentWriter(calldata);
        if (isSequencePriced)
        {
            Encode(transaction, ref writer);
        }
        else
        {
            EncodeKeys(transaction.NonceKeys!, ref writer);
        }

        int zeros = calldata.CountZeros();
        return (zeros, length - zeros);
    }

    /// <summary>Upper bound on <c>nonce_calldata</c>: a full set of 32-byte keys (33 bytes each) behind a three-byte
    /// long-form sequence header, plus a nine-byte sequence number.</summary>
    private const int MaxCalldataLength = 3 + Eip8250Constants.MaxNonceKeys * 33 + 9;
}
