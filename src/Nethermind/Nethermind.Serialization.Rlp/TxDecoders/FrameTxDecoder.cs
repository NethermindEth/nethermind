// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>
/// Decodes the EIP-8141 frame transaction payload
/// <c>[chain_id, nonce, sender, frames, signatures, max_priority_fee_per_gas, max_fee_per_gas,
/// max_fee_per_blob_gas, blob_versioned_hashes]</c>, or its EIP-8250 form, which replaces
/// <c>nonce</c> with <c>nonce_keys, nonce_seq</c>.
/// The sender is explicit in the payload — there is no envelope ECDSA signature and no recovery.
/// Encoding with <c>forSigning</c> produces the <c>compute_sig_hash</c> form: the raw signature
/// bytes of canonical-hash (empty msg) entries are elided.
/// </summary>
public sealed class FrameTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseTxDecoder<T>(TxType.FrameTx, transactionFactory) where T : Transaction, new()
{
    // EIP8141-DEVIATION: the spec does not cap the signature count (calldata cost bounds it in
    // practice); this guards against pathological allocations before gas is charged. Propose an
    // explicit MAX_SIGNATURES to the spec.
    private const int SignaturesDecodeCap = 1024;

    private static readonly RlpLimit FramesCountLimit = RlpLimit.For<Transaction>(Eip8141Constants.MaxFrames, nameof(Transaction.Frames));
    private static readonly RlpLimit SignaturesCountLimit = RlpLimit.For<Transaction>(SignaturesDecodeCap, nameof(Transaction.FrameSignatures));
    private static readonly RlpLimit NonceKeysCountLimit = RlpLimit.For<Transaction>(Eip8250Constants.MaxNonceKeys, nameof(Transaction.NonceKeys));
    // EIP8141-GAP: the spec does not bound blob_versioned_hashes; mirrors the blob tx decode cap.
    private static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(128, nameof(Transaction.BlobVersionedHashes));

    private static readonly byte[][] EmptyVersionedHashes = [];

    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // EIP8141-DEVIATION: the spec allows chain_id < 2^256; decoded as u64 like every other
        // Nethermind transaction type (codebase-wide ChainId width).
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.NonceKeys = decoderContext.IsSequenceNext() ? DecodeNonceKeys(ref decoderContext) : null;
        transaction.Nonce = decoderContext.DecodeULong();
        transaction.SenderAddress = decoderContext.DecodeAddress() ?? ThrowMissingSender();
        transaction.Frames = decoderContext.DecodeArray(TxFrameDecoder.Instance, limit: FramesCountLimit);
        transaction.FrameSignatures = decoderContext.DecodeArray(TxFrameSignatureDecoder.Instance, limit: SignaturesCountLimit);
        transaction.GasPrice = decoderContext.DecodeUInt256(); // max_priority_fee_per_gas
        transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
        transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
        transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays(BlobVersionedHashesCountLimit, innerSize: Hash256.Size);

        // A frame transaction has no gas_limit field; expose the sum of frame gas limits as GasLimit
        // so pre-execution consumers (GasLimitTxFilter, block-production selection, pre-warming) that
        // read it do not treat the transaction as ~0 gas. The processor derives the real tx_gas_limit.
        ulong gasLimit = 0;
        foreach (TxFrame frame in transaction.Frames)
        {
            gasLimit = frame.GasLimit > ulong.MaxValue - gasLimit ? ulong.MaxValue : gasLimit + frame.GasLimit;
        }
        transaction.GasLimit = gasLimit;

        if (transaction.NonceKeys is not null)
        {
            transaction.FrameCalldataStats = FrameTxNonceCalldata.Measure(transaction);
        }
    }

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            writer.StartByteArray(sequenceLength + 1, false);
        }

        writer.WriteByte((byte)Type);
        writer.StartSequence(contentLength);
        EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: forSigning);
    }

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txPayloadLength = base.GetLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping)
            ? 1 + txPayloadLength
            : Rlp.LengthOfSequence(1 + txPayloadLength);
    }

    protected override void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: false);

    private static void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, bool elideCanonicalSignatureBytes)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(transaction.ChainId ?? 0);
        FrameTxNonceCalldata.Encode(transaction, ref writer);
        writer.Encode(transaction.SenderAddress);
        TxFrameDecoder.Instance.EncodeArray(ref writer, transaction.Frames);
        TxFrameSignatureDecoder.Instance.EncodeArray(ref writer, transaction.FrameSignatures, elideCanonicalSignatureBytes);
        writer.Encode(transaction.GasPrice);
        writer.Encode(transaction.DecodedMaxFeePerGas);
        writer.Encode(transaction.MaxFeePerBlobGas.GetValueOrDefault());
        writer.Encode(transaction.BlobVersionedHashes ?? EmptyVersionedHashes);
    }

    protected override int GetContentLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning,
        bool isEip155Enabled = false, ulong chainId = 0) =>
        Rlp.LengthOf(transaction.ChainId ?? 0)
        + FrameTxNonceCalldata.Length(transaction)
        + Rlp.LengthOf(transaction.SenderAddress)
        + TxFrameDecoder.Instance.GetArrayLength(transaction.Frames)
        + TxFrameSignatureDecoder.Instance.GetArrayLength(transaction.FrameSignatures, elideCanonicalSignatureBytes: forSigning)
        + Rlp.LengthOf(transaction.GasPrice)
        + Rlp.LengthOf(transaction.DecodedMaxFeePerGas)
        + Rlp.LengthOf(transaction.MaxFeePerBlobGas.GetValueOrDefault())
        + Rlp.LengthOf(transaction.BlobVersionedHashes ?? EmptyVersionedHashes);

    protected override int GetSignatureLength(Signature? signature, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0) => 0;

    protected override void EncodeSignature<TWriter>(Signature? signature, ref TWriter writer, bool forSigning,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
    }

    // The payload is exactly 9 fields with no envelope signature (the sender is explicit). The base
    // decoder reads a trailing [v, r, s] whenever elements remain after the payload; reject that so a
    // padded encoding is not silently accepted with a spurious signature (which strict clients drop,
    // diverging on the transaction hash).
    protected override Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        throw new RlpException("frame transaction must not carry a trailing signature");

    /// <summary>Reads <c>nonce_keys</c> as a list of integers.</summary>
    /// <remarks>
    /// Not <c>DecodeArray</c>: it substitutes the default for an empty-list element, turning the wire
    /// bytes <c>c1 c0</c> into the key set <c>[0]</c> instead of rejecting them.
    /// </remarks>
    private static UInt256[] DecodeNonceKeys(ref RlpReader decoderContext)
    {
        int contentLength = decoderContext.ReadSequenceLength();
        int end = decoderContext.Position + contentLength;
        Span<UInt256> buffer = stackalloc UInt256[Eip8250Constants.MaxNonceKeys];
        int count = 0;
        while (decoderContext.Position < end)
        {
            if (count == buffer.Length)
            {
                throw new RlpLimitException($"Exceeded {NonceKeysCountLimit.CollectionExpression}");
            }

            buffer[count++] = decoderContext.DecodeUInt256();
        }

        return buffer[..count].ToArray();
    }

    [DoesNotReturn, StackTraceHidden]
    private static Address ThrowMissingSender() => throw new RlpException("frame transaction sender must be a 20-byte address");
}

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see>'s <c>nonce_calldata</c> —
/// <c>rlp(nonce_keys) || rlp(nonce_seq)</c>, the key sequence being absent on a plain-nonce transaction.
/// </summary>
/// <remarks>
/// Shared by the payload encoder and the intrinsic-gas path, which prices exactly these bytes: pricing an
/// independently written copy is what let the charge drift from the wire encoding.
/// </remarks>
public static class FrameTxNonceCalldata
{
    public static void Encode<TWriter>(Transaction transaction, ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (transaction.NonceKeys is { } nonceKeys)
        {
            writer.StartSequence(KeysContentLength(nonceKeys));
            foreach (UInt256 nonceKey in nonceKeys)
            {
                writer.Encode(nonceKey);
            }
        }

        writer.Encode(transaction.Nonce);
    }

    /// <summary>Length in bytes of what <see cref="Encode{TWriter}"/> writes for <paramref name="transaction"/>.</summary>
    public static int Length(Transaction transaction) =>
        (transaction.NonceKeys is { } nonceKeys ? Rlp.LengthOfSequence(KeysContentLength(nonceKeys)) : 0)
        + Rlp.LengthOf(transaction.Nonce);

    internal static int KeysContentLength(UInt256[] nonceKeys)
    {
        int contentLength = 0;
        foreach (UInt256 nonceKey in nonceKeys)
        {
            contentLength += Rlp.LengthOf(nonceKey);
        }

        return contentLength;
    }

    /// <summary>The zero and non-zero byte counts of what <see cref="Encode{TWriter}"/> writes, the split EIP-8141
    /// calldata pricing needs. Measured off the encoded bytes rather than recomputed, so the charge cannot drift
    /// from the wire encoding.</summary>
    public static (int ZeroBytes, int NonZeroBytes) Measure(Transaction transaction)
    {
        int length = Length(transaction);
        Span<byte> buffer = stackalloc byte[MaxCalldataLength];
        Span<byte> calldata = buffer[..length];
        RlpWriter writer = new(calldata);
        Encode(transaction, ref writer);
        int zeros = calldata.CountZeros();
        return (zeros, length - zeros);
    }

    /// <summary>Upper bound on <c>nonce_calldata</c>: a full set of 32-byte keys (33 bytes each) behind a three-byte
    /// long-form sequence header, plus a nine-byte sequence number.</summary>
    private const int MaxCalldataLength = 3 + Eip8250Constants.MaxNonceKeys * 33 + 9;
}
