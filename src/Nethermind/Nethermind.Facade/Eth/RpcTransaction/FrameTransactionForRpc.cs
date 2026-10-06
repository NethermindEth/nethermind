// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
    /// <remarks>RPC simulation accepts an entry with empty signature bytes as a placeholder and prices it as signed, so
    /// supply one per entry the signed transaction will carry; entries with signature bytes are still verified.</remarks>
    public FrameSignatureForRpc[]? Signatures { get; set; }

    /// <summary><c>max_fee_per_blob_gas</c>, a field of the signed payload unless it carries <see cref="MaxFee"/>.</summary>
    public UInt256? MaxFeePerBlobGas { get; set; }

    /// <summary>EIP-7999 <c>max_fee</c>: one budget, in wei, for all of the transaction's gas, in place of
    /// <c>maxFeePerGas</c> and <c>maxFeePerBlobGas</c>.</summary>
    public UInt256? MaxFee { get; set; }

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
        if (transaction.MaxFee is { } maxFee)
        {
            // The per-gas cap the base reported is derived, not signed.
            MaxFee = maxFee;
            MaxFeePerGas = null;
        }
        else
        {
            MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        }

        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        if (validateUserInput && MaxFee is not null && (MaxFeePerGas is not null || MaxFeePerBlobGas is not null))
            return RpcTransactionErrors.MaxFeeWithPerGasFees;

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

        // EIP-7999 admits only the max_fee shape, so a call priced per gas is converted to the budget those prices
        // buy: the per-gas cap over the frame gas plus the per-blob cap over the blob gas.
        UInt256? maxFee = MaxFee ?? (spec is { IsEip7999Enabled: true } ? BudgetFor(tx, MaxFeePerGas ?? UInt256.Zero, totalFrameGas) : null);
        if (maxFee is { } budget)
        {
            tx.MaxFee = budget;
            tx.DecodedMaxFeePerGas = FrameTxValidation.ImpliedMaxFeePerGas(budget, totalFrameGas);
            tx.MaxFeePerBlobGas = null;
        }

        return tx;
    }

    public override bool ShouldSetBaseFee() => base.ShouldSetBaseFee() || MaxFee.IsPositive();

    private static UInt256 BudgetFor(Transaction tx, in UInt256 maxFeePerGas, ulong gas) =>
        UInt256.MultiplyOverflow(maxFeePerGas, (UInt256)gas, out UInt256 gasBudget)
        || UInt256.MultiplyOverflow(tx.MaxFeePerBlobGas.GetValueOrDefault(), (UInt256)tx.GetBlobGas(), out UInt256 blobBudget)
        || UInt256.AddOverflow(gasBudget, blobBudget, out UInt256 budget)
            ? UInt256.MaxValue
            : budget;

    public new static FrameTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
