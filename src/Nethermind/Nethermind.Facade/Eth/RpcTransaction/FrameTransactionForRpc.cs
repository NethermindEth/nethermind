// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>JSON-RPC view of an EIP-8141 frame transaction: the EIP-1559 fee fields plus the frame and hoisted signature lists.</summary>
[GenerateJsonWriter(RegisterWithSerializer = false)]
[RepopulatableTransaction]
public class FrameTransactionForRpc : EIP1559TransactionForRpc, IFromTransaction<FrameTransactionForRpc>
{
    public new static TxType TxType => TxType.FrameTx;

    public override TxType? Type => TxType;

    /// <summary>EIP-8250 <c>nonce_keys</c>, sharing the sequence number reported as <c>nonce</c>.</summary>
    /// <remarks>Absent for an envelope nonce, which is a different signing payload from the empty key set.</remarks>
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

    /// <summary>A per-blob-gas price a call may give in place of <see cref="MaxFee"/>; not a field of the payload.</summary>
    public UInt256? MaxFeePerBlobGas { get; set; }

    /// <summary>EIP-8141 <c>max_fee</c>: one budget, in wei, for all of the transaction's execution, state and blob gas.</summary>
    /// <remarks>Reported in place of <c>maxFeePerGas</c>, which the payload does not carry. A call may give
    /// <c>maxFeePerGas</c> and <c>maxFeePerBlobGas</c> instead, which buy the budget those prices would.</remarks>
    public UInt256? MaxFee { get; set; }

    /// <summary><c>blob_versioned_hashes</c>, an unconditional field of the signed payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[][]? BlobVersionedHashes { get; set; }

    [JsonConstructor]
    public FrameTransactionForRpc() { }

    public FrameTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData) { }

    internal override void Populate(Transaction transaction, in TransactionForRpcContext extraData)
    {
        base.Populate(transaction, extraData);
        NonceKeys = transaction.NonceKeys;
        Frames = FrameForRpc.FromFrames(transaction.Frames);
        Signatures = FrameSignatureForRpc.FromSignatures(transaction.FrameSignatures);

        // Covered by the sig hash, so always reported: a consumer must be able to rebuild the payload.
        // The per-gas cap the base reported is derived, not signed.
        MaxFee = transaction.MaxFee;
        MaxFeePerGas = null;
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
        tx.BlobVersionedHashes = BlobVersionedHashes;
        // A call priced per gas is converted to the budget those prices buy: the per-gas cap over max_gas plus the
        // per-blob cap over the blob gas.
        tx.MaxFee = MaxFee ?? BudgetFor(tx, MaxFeePerGas ?? GasPrice ?? UInt256.Zero, MaxFeePerBlobGas ?? UInt256.Zero, spec);
        tx.DecodedMaxFeePerGas = FrameTxValidation.ImpliedMaxFeePerGas(tx.MaxFee, totalFrameGas);
        return tx;
    }

    public override bool ShouldSetBaseFee() => base.ShouldSetBaseFee() || MaxFee.IsPositive();

    /// <inheritdoc/>
    /// <remarks>A request naming a <c>max_fee</c> gets only the priority fee, since <c>max_fee</c> is its fee cap.</remarks>
    public override Result FillDefaults(in TxFillContext context)
    {
        if (MaxFee is null)
            return base.FillDefaults(context);

        MaxPriorityFeePerGas ??= context.MaxPriorityFeePerGas;
        return Result.Success;
    }

    /// <summary>The budget a call priced per gas buys over <paramref name="maxGas"/>, or <see langword="null"/> for one
    /// naming a <c>max_fee</c>.</summary>
    /// <remarks>For a run whose <c>max_gas</c> is not known when the call is converted, such as a frame gas search.</remarks>
    public UInt256? PerGasBudget(Transaction tx, ulong maxGas) =>
        MaxFee is null ? Price(tx, MaxFeePerGas ?? GasPrice ?? UInt256.Zero, MaxFeePerBlobGas ?? UInt256.Zero, maxGas) : null;

    /// <remarks>Without a fork to price <c>max_gas</c> by, the frame gas sum stands in.</remarks>
    private static UInt256 BudgetFor(Transaction tx, in UInt256 maxFeePerGas, in UInt256 maxFeePerBlobGas, IReleaseSpec? spec)
    {
        if (maxFeePerGas.IsZero && maxFeePerBlobGas.IsZero)
        {
            return UInt256.Zero;
        }

        if (spec is null)
        {
            return Price(tx, maxFeePerGas, maxFeePerBlobGas, tx.GasLimit);
        }

        // The measurements below assume the processor's structural checks; a transaction failing them is left for the
        // processor to reject with its own error, and any budget will do.
        if (!FrameTxValidation.IsWellFormed(tx, spec.IsEip7906Enabled, out _)
            || (tx.NonceKeys is { } nonceKeys && !KeyedNonceManager.AreNonceKeysWellFormed(nonceKeys)))
        {
            return UInt256.MaxValue;
        }

        // Measured as the processor does before pricing, so max_gas here is the one the budget is checked against.
        if (tx.NonceKeys is not null) tx.FrameCalldataStats = FrameTxNonceCalldata.Measure(tx);

        return FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong maxGas, estimateSignatureBytes: true)
            ? Price(tx, maxFeePerGas, maxFeePerBlobGas, maxGas)
            : UInt256.MaxValue;
    }

    private static UInt256 Price(Transaction tx, in UInt256 maxFeePerGas, in UInt256 maxFeePerBlobGas, ulong maxGas) =>
        UInt256.MultiplyOverflow(maxFeePerGas, (UInt256)maxGas, out UInt256 gasBudget)
        || UInt256.MultiplyOverflow(maxFeePerBlobGas, (UInt256)tx.GetBlobGas(), out UInt256 blobBudget)
        || UInt256.AddOverflow(gasBudget, blobBudget, out UInt256 budget)
            ? UInt256.MaxValue
            : budget;

    public new static FrameTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
