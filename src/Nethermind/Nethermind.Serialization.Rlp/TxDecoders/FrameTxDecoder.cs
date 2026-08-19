// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>
/// Decodes the EIP-8141 frame transaction payload
/// <c>[chain_id, nonce, sender, frames, signatures, max_priority_fee_per_gas, max_fee_per_gas,
/// max_fee_per_blob_gas, blob_versioned_hashes]</c>.
/// The sender is explicit in the payload — there is no envelope ECDSA signature and no recovery.
/// Encoding with <c>forSigning</c> produces the <c>compute_sig_hash</c> form: the raw signature
/// bytes of canonical-hash (empty msg) entries are elided.
/// </summary>
/// <remarks>The wrapper and plain forms are disjoint: a wrapper opens with a list, a plain payload
/// with the <c>chain_id</c> scalar.</remarks>
public sealed class FrameTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseTxDecoder<T>(TxType.FrameTx, transactionFactory) where T : Transaction, new()
{
    // EIP8141-DEVIATION: the spec does not cap the signature count (calldata cost bounds it in
    // practice); this guards against pathological allocations before gas is charged. Propose an
    // explicit MAX_SIGNATURES to the spec.
    private const int SignaturesDecodeCap = 1024;

    private static readonly RlpLimit FramesCountLimit = RlpLimit.For<Transaction>(Eip8141Constants.MaxFrames, nameof(Transaction.Frames));
    private static readonly RlpLimit SignaturesCountLimit = RlpLimit.For<Transaction>(SignaturesDecodeCap, nameof(Transaction.FrameSignatures));
    // EIP8141-GAP: the spec does not bound blob_versioned_hashes; mirrors the blob tx decode cap.
    private static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(ShardBlobNetworkWrapperRlp.BlobCountLimit, nameof(Transaction.BlobVersionedHashes));

    private static readonly byte[][] EmptyVersionedHashes = [];


    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            if (IsNetworkWrapper(ref decoderContext))
            {
                DecodeNetworkWrapper(ref transaction, ref decoderContext, rlpBehaviors);
                return;
            }

            base.Decode(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
            // EIP-7594: as for type-3, a blob-carrying transaction's mempool form is the sidecar wrapper.
            if (transaction is { CarriesBlobs: true }) ThrowMissingSidecar();
            return;
        }

        base.Decode(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingSidecar() =>
        throw new RlpException($"Blob-carrying {nameof(TxType.FrameTx)} in mempool form must carry a {nameof(ShardBlobNetworkWrapper)}");

    private static bool IsNetworkWrapper(ref RlpReader decoderContext)
    {
        int start = decoderContext.Position;
        int length = decoderContext.ReadSequenceLength();
        // The reader spans the whole message, so bound the peek by this transaction's own sequence.
        int end = Math.Min(decoderContext.Position + length, decoderContext.Length);
        bool isWrapper = decoderContext.Position < end && decoderContext.IsSequenceNext();
        decoderContext.Position = start;
        return isWrapper;
    }

    private void DecodeNetworkWrapper(ref Transaction? transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors)
    {
        int networkWrapperLength = decoderContext.ReadSequenceLength();
        int networkWrapperCheck = decoderContext.Position + networkWrapperLength;
        int rlpLength = decoderContext.PeekNextRlpLength();
        int txSequenceStart = decoderContext.Position;
        ReadOnlySpan<byte> transactionSequence = decoderContext.Peek(rlpLength);

        base.Decode(ref transaction, txSequenceStart, transactionSequence, ref decoderContext,
            (rlpBehaviors | RlpBehaviors.ExcludeHashes) & ~RlpBehaviors.InMempoolForm);

        if (transaction is not null)
        {
            transaction.NetworkWrapper = ShardBlobNetworkWrapperRlp.Decode(ref decoderContext);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
            {
                decoderContext.Check(networkWrapperCheck);
            }

            if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                transaction.Hash = CalculateHashForNetworkPayloadForm(transactionSequence);
            }
        }
    }

    private static Hash256 CalculateHashForNetworkPayloadForm(ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> txType = [(byte)TxType.FrameTx];
        hash.Update(txType);
        hash.Update(transactionSequence);
        return new Hash256(hash.GenerateValueHash());
    }


    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // EIP8141-DEVIATION: the spec allows chain_id < 2^256; decoded as u64 like every other
        // Nethermind transaction type (codebase-wide ChainId width).
        transaction.ChainId = decoderContext.DecodeULong();
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
            ulong frameLimit = frame.ExecutionGasLimit > ulong.MaxValue - frame.StateGasLimit
                ? ulong.MaxValue
                : frame.ExecutionGasLimit + frame.StateGasLimit;
            gasLimit = frameLimit > ulong.MaxValue - gasLimit ? ulong.MaxValue : gasLimit + frameLimit;
        }
        transaction.GasLimit = gasLimit;
    }

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int payloadContentLength = GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        int payloadSequenceLength = Rlp.LengthOfSequence(payloadContentLength);

        // A sidecar-less blob carrier still serialises to the plain form that Decode refuses; see GetLength.
        ShardBlobNetworkWrapper? wrapper = rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
            ? transaction.NetworkWrapper as ShardBlobNetworkWrapper
            : null;

        int wrapperContentLength = wrapper is null ? 0 : payloadSequenceLength + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper);
        int bodyLength = wrapper is null ? payloadSequenceLength : Rlp.LengthOfSequence(wrapperContentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            writer.StartByteArray(bodyLength + 1, false);
        }

        writer.WriteByte((byte)Type);

        if (wrapper is null)
        {
            writer.StartSequence(payloadContentLength);
            EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: forSigning);
        }
        else
        {
            writer.StartSequence(wrapperContentLength);
            writer.StartSequence(payloadContentLength);
            // Elide on forSigning like payloadContentLength, or the declared length and the bytes drift.
            EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: forSigning);
            ShardBlobNetworkWrapperRlp.Encode(ref writer, wrapper);
        }
    }

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
        int payloadSequenceLength = base.GetLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);

        // GetLength is the pool's sizing call, so it must stay total and Encode must agree with it.
        ShardBlobNetworkWrapper? wrapper = rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
            ? transaction.NetworkWrapper as ShardBlobNetworkWrapper
            : null;

        int bodyLength = wrapper is null
            ? payloadSequenceLength
            : Rlp.LengthOfSequence(payloadSequenceLength + ShardBlobNetworkWrapperRlp.GetFieldsLength(wrapper));

        return rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping)
            ? 1 + bodyLength
            : Rlp.LengthOfSequence(1 + bodyLength);
    }

    protected override void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: false);

    private static void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, bool elideCanonicalSignatureBytes)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(transaction.ChainId ?? 0);
        writer.Encode(transaction.Nonce);
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
        + Rlp.LengthOf(transaction.Nonce)
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

    [DoesNotReturn, StackTraceHidden]
    private static Address ThrowMissingSender() => throw new RlpException("frame transaction sender must be a 20-byte address");
}
