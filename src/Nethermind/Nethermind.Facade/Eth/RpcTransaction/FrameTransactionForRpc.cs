// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>JSON-RPC view of an EIP-8141 frame transaction: the EIP-1559 fee fields plus the frame and hoisted signature lists.</summary>
public class FrameTransactionForRpc : EIP1559TransactionForRpc, IFromTransaction<FrameTransactionForRpc>
{
    public new static TxType TxType => TxType.FrameTx;

    public override TxType? Type => TxType;

    /// <summary>EIP-8250 <c>nonce_keys</c>, sharing the sequence number reported as <c>nonce</c>.</summary>
    /// <remarks>Absent for an envelope nonce, which is a different signing payload from the key set <c>[0]</c>.</remarks>
    public UInt256[]? NonceKeys { get; set; }

    /// <summary>EIP-8141 <c>frames</c>, in execution order; the field that discriminates this envelope.</summary>
    /// <remarks>The transaction's <c>gas</c> is their summed limits, so a frame's own budget is read here
    /// rather than from that total.</remarks>
    [JsonDiscriminator]
    public FrameForRpc[]? Frames { get; set; }

    /// <summary>EIP-8141 <c>signatures</c>: the entries hoisted out of the frames, verified before any frame
    /// runs and read by frame code through <c>SIGPARAM</c> by index into this list.</summary>
    public FrameSignatureForRpc[]? Signatures { get; set; }

    /// <summary><c>max_fee_per_blob_gas</c>, an unconditional field of the signed payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerBlobGas { get; set; }

    /// <summary><c>blob_versioned_hashes</c>, an unconditional field of the signed payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[][]? BlobVersionedHashes { get; set; }

    public RecentRootReferenceForRpc[]? RecentRootReferences { get; set; }

    [JsonConstructor]
    public FrameTransactionForRpc() { }

    public FrameTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        NonceKeys = transaction.NonceKeys;
        Frames = FrameForRpc.FromFrames(transaction.Frames);
        Signatures = FrameSignatureForRpc.FromSignatures(transaction.FrameSignatures);
        RecentRootReferences = RecentRootReferenceForRpc.FromReferences(transaction.RecentRootReferences);

        // Covered by the sig hash, so always reported: a consumer must be able to rebuild the payload.
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (baseResult.IsError) return baseResult;

        if (!FrameForRpc.TryToFrames(Frames, out TxFrame[]? frames))
            return RpcTransactionErrors.NullEntryIn("frames");

        if (!FrameSignatureForRpc.TryToSignatures(Signatures, out TxFrameSignature[]? signatures))
            return RpcTransactionErrors.NullEntryIn("signatures");

        if (!RecentRootReferenceForRpc.TryToReferences(RecentRootReferences, out RecentRootReference[]? references))
            return RpcTransactionErrors.NullEntryIn("recentRootReferences");

        // The caller's gas field is not what this type spends, so the cap the base applied to
        // Transaction.GasLimit leaves the work a frame transaction asks for unbounded.
        ulong effectiveCap = gasCap.EffectiveGasCap();
        ulong totalFrameGas = FrameTxValidation.TotalGasLimit(frames);
        // A bound on work, not the on-chain price TryCalculateGasBudget derives: the processor verifies every
        // entry before any budget exists, so an unpriced signature list buys recoveries no frame pays for.
        ulong signatureGas = FrameTxValidation.SignatureVerificationWorkGas(signatures);
        ulong reservedGas = totalFrameGas > ulong.MaxValue - signatureGas ? ulong.MaxValue : totalFrameGas + signatureGas;
        if (reservedGas > effectiveCap)
            return RpcTransactionErrors.FrameGasAboveCap(totalFrameGas, signatureGas, effectiveCap);

        Transaction tx = baseResult.Data;
        // The invariant FrameTxDecoder establishes for a decoded frame tx, so GasLimit readers see the same
        // value whichever path built it. The processor still derives the real budget from the frames.
        tx.GasLimit = totalFrameGas;
        tx.NonceKeys = NonceKeys;
        tx.Frames = frames;
        tx.FrameSignatures = signatures;
        tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
        tx.BlobVersionedHashes = BlobVersionedHashes;
        tx.RecentRootReferences = references;
        return tx;
    }

    public new static FrameTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
