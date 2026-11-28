// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>Decodes the EIP-8141 frame transaction payload <c>[chain_id, nonce, sender, claims, frames, fees,
/// blob_versioned_hashes]</c>, where <c>fees = [max_fee, max_priority_fee_per_gas]</c>,
/// with EIP-8250's <c>nonce_keys, nonce_seq</c> in place of <c>nonce</c>.</summary>
/// <remarks>The sender is explicit, so there is no envelope signature or recovery. The mempool form is the pooled
/// wrapper <c>[tx_body, wrapper_version, witnesses, blob_sidecar]</c>, which carries the segregated claim witnesses
/// and the optional EIP-7594 sidecar. The wrapper and plain forms are disjoint: a wrapper opens with a list, a plain
/// payload with the <c>chain_id</c> scalar.</remarks>
public sealed class FrameTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseTxDecoder(TxType.FrameTx, transactionFactory)
{
    private static readonly RlpLimit FramesCountLimit = RlpLimit.For<Transaction>(Eip8141Constants.MaxFrames, nameof(Transaction.Frames));
    private static readonly RlpLimit ClaimsCountLimit = RlpLimit.For<Transaction>(Eip8141Constants.MaxClaims, nameof(Transaction.FrameClaims));
    private static readonly RlpLimit WitnessesCountLimit = RlpLimit.For<Transaction>(Eip8141Constants.MaxClaims, nameof(Transaction.ClaimWitnesses));

    // Decode-side allocation guard only — EIP-7594's per-tx blob limit is far tighter and is
    // enforced by the transaction validator.
    private static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(ShardBlobNetworkWrapperRlp.BlobCountLimit, nameof(Transaction.BlobVersionedHashes));

    public override void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        try
        {
            DecodeFrameTransaction(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
        }
        catch (RlpReaderException e) when (e.Message == RlpReader.TruncatedMessage)
        {
            throw new RlpReaderException("RLP data is truncated: frame transaction payload is incomplete.", e);
        }
    }

    private void DecodeFrameTransaction(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
    {
        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) != 0)
        {
            bool isWrapper = rlpBehaviors.IsTrustedInput()
                ? DecodeIfPooledWrapper<OffFlag>(ref transaction, ref reader, source, rlpBehaviors)
                : DecodeIfPooledWrapper<OnFlag>(ref transaction, ref reader, source, rlpBehaviors);
            if (isWrapper) return;

            DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
            // Only a transaction with nothing to segregate may travel without the pooled wrapper.
            if (transaction is { CarriesBlobs: true } || transaction?.FrameClaims is { Length: > 0 }) ThrowMissingWrapper();
            return;
        }

        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowShortFeeList() =>
        throw new RlpReaderException($"{nameof(TxType.FrameTx)} fees must hold two items");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingWrapper() =>
        throw new RlpReaderException($"{nameof(TxType.FrameTx)} with claims or blobs in mempool form must carry the pooled wrapper");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnknownWrapperVersion(byte version) =>
        throw new RlpReaderException($"Unknown {nameof(TxType.FrameTx)} pooled wrapper version {version}");

    private bool DecodeIfPooledWrapper<TCanonicalChecks>(ref Transaction? transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader wrapper = reader.EnterSequence<TCanonicalChecks>(out int wrapperLength);
        if (!wrapper.HasNext || !wrapper.IsNextSequence) return false;

        RlpReader peek = wrapper;
        ReadOnlySpan<byte> transactionSequence = peek.ReadEncodedItem<TCanonicalChecks>();

        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref wrapper, source,
            (rlpBehaviors | RlpBehaviors.ExcludeHashes) & ~RlpBehaviors.InMempoolForm);

        byte version = wrapper.DecodeByte<TCanonicalChecks>();
        if (version != Eip8141Constants.PooledWrapperVersion) ThrowUnknownWrapperVersion(version);

        transaction!.ClaimWitnesses = wrapper.DecodeByteArrays<TCanonicalChecks>(WitnessesCountLimit);

        RlpReader sidecar = wrapper.EnterSequence<TCanonicalChecks>(out int sidecarLength);
        if (sidecar.HasNext)
        {
            // EIP-7594: the sidecar carries cell proofs, so the version the blob wrapper names elsewhere is implied.
            transaction.NetworkWrapper = ShardBlobNetworkWrapperRlp.Decode<TCanonicalChecks>(ref sidecar, rlpBehaviors, ProofVersion.V1);
        }

        wrapper.ExitSequence(in sidecar, sidecarLength, rlpBehaviors);
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
        // The frames and claims never took the other behaviors.
        RlpBehaviors itemBehaviors = rlpBehaviors & RlpBehaviors.TrustedInput;

        // EIP8141-DEVIATION: the spec allows chain_id < 2^256; decoded as u64, the codebase-wide ChainId width.
        transaction.ChainId = reader.DecodeULong<TCanonicalChecks>();
        transaction.NonceKeys = reader.IsNextSequence ? FrameTxNonceCalldata.DecodeKeys<TCanonicalChecks>(ref reader) : null;
        transaction.Nonce = DecodeNonce<TCanonicalChecks>(ref reader);
        transaction.SenderAddress = reader.DecodeAddress();
        transaction.FrameClaims = DecodeClaims<TCanonicalChecks>(ref reader, source, itemBehaviors);
        transaction.Frames = DecodeFrames<TCanonicalChecks>(ref reader, source, itemBehaviors);
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

    private static TxFrameClaim[] DecodeClaims<TCanonicalChecks>(ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        TxFrameClaim[] claims = new TxFrameClaim[items.CountItems<TCanonicalChecks>(ClaimsCountLimit)];
        for (int i = 0; i < claims.Length; i++)
        {
            if (items.IsNextItemEmptyList()) ThrowNullArrayElement(i);
            claims[i] = TxFrameClaimDecoder.Decode<TCanonicalChecks>(ref items, source, rlpBehaviors);
        }

        reader.ExitSequence(in items, itemLength);
        return claims;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNullArrayElement(int index) =>
        throw new RlpReaderException($"Unexpected null array element at index {index}");

    public override void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors);

    /// <summary>Whether <paramref name="transaction"/> encodes as the pooled wrapper under <paramref name="rlpBehaviors"/>.</summary>
    /// <remarks>Only in mempool form, and only with something to segregate: a transaction that lost its witnesses, as one
    /// taken out of a block whose proof was not kept, still serialises, to the plain form that Decode refuses.</remarks>
    private static bool UsesPooledWrapper(Transaction transaction, RlpBehaviors rlpBehaviors) =>
        rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
        && (transaction.ClaimWitnesses is not null || transaction.NetworkWrapper is ShardBlobNetworkWrapper);

    internal static void EncodeTransaction(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors)
    {
        int payloadContentLength = GetContentLength(transaction);
        int payloadSequenceLength = Rlp.LengthOfSequence(payloadContentLength);

        if (!UsesPooledWrapper(transaction, rlpBehaviors))
        {
            StartTypedTransaction(ref writer, TxType.FrameTx, payloadSequenceLength, rlpBehaviors);
            RlpSequence sequence = writer.StartSequence(payloadContentLength);
            EncodePayload(transaction, ref writer);
            writer.EndSequence(sequence);
            return;
        }

        ShardBlobNetworkWrapper? blobs = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        int sidecarContentLength = GetSidecarContentLength(blobs, rlpBehaviors);
        int wrapperContentLength = GetWrapperContentLength(transaction, payloadSequenceLength, sidecarContentLength);
        StartTypedTransaction(ref writer, TxType.FrameTx, Rlp.LengthOfSequence(wrapperContentLength), rlpBehaviors);

        RlpSequence wrapperSequence = writer.StartSequence(wrapperContentLength);
        RlpSequence payloadSequence = writer.StartSequence(payloadContentLength);
        EncodePayload(transaction, ref writer);
        writer.EndSequence(payloadSequence);
        writer.Encode((ulong)Eip8141Constants.PooledWrapperVersion);
        writer.Encode(transaction.ClaimWitnesses ?? []);
        RlpSequence sidecarSequence = writer.StartSequence(sidecarContentLength);
        if (blobs is not null) ShardBlobNetworkWrapperRlp.Encode(ref writer, blobs, rlpBehaviors, writeVersion: false);
        writer.EndSequence(sidecarSequence);
        writer.EndSequence(wrapperSequence);
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors)
    {
        int payloadSequenceLength = Rlp.LengthOfSequence(GetContentLength(transaction));

        // GetLength is the pool's sizing call, so it must stay total and Encode must agree with it.
        int bodyLength = UsesPooledWrapper(transaction, rlpBehaviors)
            ? Rlp.LengthOfSequence(GetWrapperContentLength(transaction, payloadSequenceLength,
                GetSidecarContentLength(transaction.NetworkWrapper as ShardBlobNetworkWrapper, rlpBehaviors)))
            : payloadSequenceLength;

        return GetTypedTransactionLength(bodyLength, rlpBehaviors);
    }

    private static int GetWrapperContentLength(Transaction transaction, int payloadSequenceLength, int sidecarContentLength) =>
        payloadSequenceLength
        + Rlp.LengthOf((ulong)Eip8141Constants.PooledWrapperVersion)
        + Rlp.LengthOf(transaction.ClaimWitnesses ?? [])
        + Rlp.LengthOfSequence(sidecarContentLength);

    private static int GetSidecarContentLength(ShardBlobNetworkWrapper? blobs, RlpBehaviors rlpBehaviors) =>
        blobs is null ? 0 : ShardBlobNetworkWrapperRlp.GetFieldsLength(blobs, rlpBehaviors, includeVersion: false);

    /// <summary>Writes the payload fields, <c>tx_body</c> without its list header.</summary>
    internal static void EncodePayload(Transaction transaction, ref RlpWriter writer)
    {
        writer.Encode(transaction.ChainId ?? 0);
        FrameTxNonceCalldata.Encode(transaction, ref writer);
        writer.Encode(transaction.SenderAddress);
        TxFrameClaimDecoder.Instance.EncodeArray(ref writer, transaction.FrameClaims);
        TxFrameDecoder.Instance.EncodeArray(ref writer, transaction.Frames);
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

    /// <summary>Length of the payload fields, <c>tx_body</c> without its list header.</summary>
    internal static int GetContentLength(Transaction transaction) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + FrameTxNonceCalldata.Length(transaction)
        + Rlp.LengthOf(transaction.SenderAddress)
        + TxFrameClaimDecoder.Instance.GetArrayLength(transaction.FrameClaims)
        + TxFrameDecoder.Instance.GetArrayLength(transaction.Frames)
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
