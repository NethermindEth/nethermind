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
    private static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(128, nameof(Transaction.BlobVersionedHashes));

    private static readonly byte[][] EmptyVersionedHashes = [];

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
    }

    public override void Encode<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        writer.StartSequence(GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId));
        EncodePayload(transaction, ref writer, elideCanonicalSignatureBytes: forSigning);
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

    [DoesNotReturn, StackTraceHidden]
    private static Address ThrowMissingSender() => throw new RlpException("frame transaction sender must be a 20-byte address");
}
