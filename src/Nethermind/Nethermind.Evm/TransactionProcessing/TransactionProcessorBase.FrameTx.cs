// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// EIP-8141 frame transaction outer loop. Runs pre-flight (nonce + signature validation), then each
/// frame as its own EVM call under the frame-transaction execution context, applying APPROVE effects,
/// enforcing the payer gate, charging spec gas, and producing per-frame receipts.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public abstract partial class TransactionProcessorBase<TGasPolicy>
{
    private TransactionResult ExecuteFrameTx(Transaction tx, ITxTracer tracer, ExecutionOptions opts, BlockHeader header, IReleaseSpec spec)
    {
        Address sender = tx.SenderAddress!;
        Snapshot txSnapshot = WorldState.TakeSnapshot();

        // Pre-flight: nonce and protocol-validated signatures.
        UInt256 accountNonce = WorldState.GetNonce(sender);
        if (accountNonce != tx.Nonce)
        {
            TransactionResult.ErrorType nonceError = tx.Nonce < accountNonce
                ? TransactionResult.ErrorType.TransactionNonceTooLow
                : TransactionResult.ErrorType.TransactionNonceTooHigh;
            return nonceError.WithDetail("frame transaction nonce mismatch");
        }

        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        // EIP-7928: resolved without recording an account access - a tx that never takes the P256
        // signature branch never accesses the precompile, so it must not enter the BAL.
        IPrecompile? p256Precompile = _codeInfoRepository.GetPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress, spec);
        if (!FrameTxSignatureValidator.Validate(tx, in sigHash, Ecdsa, p256Precompile, spec, out string? signatureError))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail(signatureError!);
        }

        TxFrame[] frames = tx.Frames!;
        UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out _);
        UInt256 premiumPerGas = UInt256.Zero;
        if (ShouldValidateGas(tx, opts) && !TryCalculatePremiumPerGas(tx, header.BaseFeePerGas, out premiumPerGas))
        {
            TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
            return TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee.WithDetail(
                $"max fee per gas less than block base fee: address {tx.SenderAddress?.ToString(withEip55Checksum: true) ?? "unknown"}, maxFeePerGas: {tx.MaxFeePerGas}, baseFee: {header.BaseFeePerGas}");
        }

        // Spec gas: tx_gas_limit = intrinsic + per-frame + calldata + signature verification
        // + sum(frame.gas_limit); the sum is overflow-checked so the processor does not depend
        // on static validation having run.
        ulong intrinsicGas = CalculateFrameTxIntrinsicGas(tx, frames, spec, out ulong floorGas);
        ulong totalFrameGas = 0;
        foreach (TxFrame frame in frames)
        {
            ulong accumulated = totalFrameGas + frame.GasLimit;
            if (accumulated < totalFrameGas)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("total frame gas overflows");
            }

            totalFrameGas = accumulated;
        }

        ulong txGasLimit = intrinsicGas + totalFrameGas;
        if (txGasLimit < intrinsicGas)
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction gas limit overflows");
        }

        // max_gas: frames reserving less than the calldata floor raise the reservation rather than
        // invalidate the transaction. The headroom is reserved and refunded, never spendable.
        txGasLimit = Math.Max(txGasLimit, floorGas);

        // max_cost (TXPARAM 0x06) reserves at max_fee_per_gas plus the blob fee, not the effective
        // price, so it is not under-reserved; settlement below charges the effective price. Bounded
        // like BuyGas: premium <= max fee and spentGas <= txGasLimit, so this product also bounds the
        // settlement products (spentCost, fees) below.
        // EIP-8141/EIP-4844: the blob leg is priced at the actual blob_base_fee (not max_fee_per_blob_gas)
        // so the mid-tx escrow the payer sees is correct; the burned blob fee cancels in the refund below.
        UInt256 blobFee = UInt256.Zero;
        if (tx.BlobVersionedHashes is { Length: > 0 })
        {
            if (!_blobBaseFeeCalculator.TryCalculateBlobFees(header, tx, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas, out blobFee))
            {
                TraceLogInvalidTx(tx, "BLOB_BASE_FEE_OVERFLOW");
                return RequiredBalanceExceeds256Bits(tx);
            }

            // EIP-4844: max_fee_per_blob_gas must cover the current blob base fee, else the tx is invalid.
            if (tx.MaxFeePerBlobGas.GetValueOrDefault() < feePerBlobGas)
            {
                TraceLogInvalidTx(tx, "INSUFFICIENT_MAX_FEE_PER_BLOB_GAS");
                return TransactionResult.ErrorType.InsufficientSenderBalance.WithDetail(
                    BlockErrorMessages.InsufficientMaxFeePerBlobGas(tx.SenderAddress, tx.MaxFeePerBlobGas, feePerBlobGas));
            }
        }

        if (UInt256.MultiplyOverflow((UInt256)txGasLimit, tx.DecodedMaxFeePerGas, out UInt256 maxCost)
            || UInt256.AddOverflow(maxCost, blobFee, out maxCost))
        {
            TraceLogInvalidTx(tx, "INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE");
            return RequiredBalanceExceeds256Bits(tx);
        }

        FrameTxContext frameContext = new(
            sender,
            tx.Nonce,
            frames,
            tx.FrameSignatures ?? [],
            sigHash,
            in maxCost,
            in tx.MaxPriorityFeePerGas,
            tx.DecodedMaxFeePerGas,
            tx.MaxFeePerBlobGas.GetValueOrDefault());

        TxFrameReceipt[] frameReceipts = new TxFrameReceipt[frames.Length];
        ulong totalFrameGasUsed = 0;
        // EIP-3529 storage refunds accumulate into a single transaction-scoped counter (ethereum/EIPs#11940).
        long refundCounter = 0;

        // EIP-2929 warm/cold journal shared across frames (spec: Cross-frame interactions).
        // Frame targets are warmed per frame; the sender and coinbase once per transaction, like
        // origin/coinbase on a regular transaction. EIP8141: whether ENTRY_POINT-as-caller should
        // be warm is unspecified — left cold.
        using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);
        if (spec.UseHotAndColdStorage)
        {
            if (spec.AddCoinbaseToTxAccessList)
            {
                accessTracker.WarmUp(header.GasBeneficiary!);
            }

            accessTracker.WarmUp(sender);
        }

        // Atomic batch state: a maximal contiguous run [i, j] where i..j-1 have ATOMIC_BATCH_FLAG
        // and j does not. On any failure inside the run, state rolls back to before the run began
        // and the remaining frames are skipped (spec: Behavior, atomic batch).
        bool inBatch = false;
        Snapshot batchStartSnapshot = default;
        StackAccessTracker batchTracker = default;
        int batchStartIndex = 0;
        Address? batchStartPayer = null;
        bool batchStartSenderApproved = false;
        long batchStartRefund = 0;

        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];
            frameContext.CurrentFrameIndex = i;

            // A batch begins at the first flagged frame; snapshot the state and log count before it.
            if (!inBatch && frame.IsAtomicBatch)
            {
                inBatch = true;
                batchStartSnapshot = WorldState.TakeSnapshot();
                batchTracker = accessTracker;
                batchTracker.TakeSnapshot();
                batchStartIndex = i;
                batchStartPayer = frameContext.Payer;
                batchStartSenderApproved = frameContext.SenderApproved;
                batchStartRefund = refundCounter;
            }

            // Transient storage (TSTORE/TLOAD) is discarded between frames (spec: Cross-frame
            // interactions); resetting at frame entry also covers the first frame.
            WorldState.ResetTransient();

            bool isSender = frame.Mode == TxFrame.ModeSender;
            if (isSender && !frameContext.SenderApproved)
            {
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("SENDER frame before execution approval");
            }

            Address resolvedTarget = frame.Target ?? sender;
            Address caller = isSender ? sender : Eip8141Constants.EntryPointAddress;
            bool isStatic = frame.Mode == TxFrame.ModeVerify;

            // ORIGIN returns the frame's caller throughout all call depths.
            VirtualMachine.SetTxExecutionContext(new TxExecutionContext(
                caller, _codeInfoRepository, tx.BlobVersionedHashes, in effectiveGasPrice, frameContext));

            // The shared journal accumulates logs across frames; this frame's own logs start here.
            int frameLogStart = accessTracker.Logs.Count;
            TransactionSubstate substate = ExecuteFrame(frame, resolvedTarget, caller, isStatic, frameContext, in accessTracker, spec, tracer, out ulong frameGasUsed);
            totalFrameGasUsed += frameGasUsed;

            bool frameSucceeded = !substate.ShouldRevert && !substate.IsError;
            if (frameSucceeded)
            {
                frameContext.MarkFrameSucceeded(i);
                // A reverted frame's refunds are discarded with its state; only successful frames
                // contribute (ethereum/EIPs#11940). An in-batch contribution is unwound below.
                refundCounter += substate.Refund;
            }

            int frameLogCount = accessTracker.Logs.Count - frameLogStart;
            LogEntry[] frameLogs;
            if (frameSucceeded && frameLogCount > 0)
            {
                frameLogs = new LogEntry[frameLogCount];
                int skipped = 0;
                int written = 0;
                foreach (LogEntry log in accessTracker.Logs)
                {
                    if (skipped < frameLogStart)
                    {
                        skipped++;
                        continue;
                    }

                    frameLogs[written++] = log;
                }
            }
            else
            {
                frameLogs = [];
            }
            frameReceipts[i] = new TxFrameReceipt(
                frameSucceeded ? TxFrameReceipt.StatusSuccess : TxFrameReceipt.StatusFailure,
                frameGasUsed,
                frameLogs);

            if (frame.Mode == TxFrame.ModeVerify && !frameSucceeded)
            {
                // A failed VERIFY frame invalidates the whole transaction.
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("VERIFY frame reverted");
            }

            if (frameSucceeded)
            {
                ApplyApproval(frameContext, resolvedTarget, spec);
            }
            else
            {
                // An APPROVE that terminated an inner call can leave a signal behind even though
                // the enclosing frame reverted; its effects must not apply.
                frameContext.ApprovalScopeSignal = 0;
            }

            if (inBatch)
            {
                if (!frameSucceeded)
                {
                    // Unroll the batch: restore pre-batch state and mark remaining frames skipped
                    // (status 0x2, gas refunded by not being consumed). The failed frame keeps its
                    // failure receipt.
                    WorldState.Restore(batchStartSnapshot);
                    batchTracker.Restore();

                    // Discard the logs of frames that ran before the failure, along with their state
                    // (ethereum/EIPs#12008), keeping status and gas_used. The tx log set is derived
                    // from these receipts after the loop, so cleared frames drop out of the bloom too.
                    for (int s = batchStartIndex; s < i; s++)
                    {
                        TxFrameReceipt earlier = frameReceipts[s];
                        if (earlier.Logs.Length > 0)
                        {
                            frameReceipts[s] = new TxFrameReceipt(earlier.Status, earlier.GasUsed, []);
                        }
                    }
                    // EIP-8141 (ethereum/EIPs#11955): a failed batch unrolls ALL effects of any APPROVE
                    // it contained. Restore reverts the payer debit and sender nonce (world state); the
                    // approval context (payer, sender_approved) and refund counter are not world state,
                    // so roll them back to their pre-batch values too. Without this, the payer field
                    // would survive a reverted charge and the terminal gate would refund uncollected
                    // funds. If the payer was only set inside the batch, it is now unset again and the
                    // gate below rejects the transaction.
                    frameContext.Payer = batchStartPayer;
                    frameContext.SenderApproved = batchStartSenderApproved;
                    refundCounter = batchStartRefund;

                    int terminal = i;
                    while (terminal < frames.Length && frames[terminal].IsAtomicBatch) terminal++;
                    for (int s = i + 1; s <= terminal && s < frames.Length; s++)
                    {
                        frameReceipts[s] = new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, []);
                        frameContext.MarkFrameSkipped(s);
                    }

                    i = terminal;
                    inBatch = false;
                }
                else if (!frame.IsAtomicBatch)
                {
                    // Terminal frame reached without failure — the batch committed.
                    inBatch = false;
                }
            }
        }

        if (frameContext.Payer is null)
        {
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction never set a payer");
        }

        // EIP-3529 storage refunds are netted once at the transaction level (ethereum/EIPs#11940):
        // the accumulated counter is capped at a fifth of the gross gas and subtracted here. Per-frame
        // receipts stay gross; only this transaction total is netted. The EIP-7623 floor then bounds
        // the net charge from below.
        ulong grossGas = intrinsicGas + totalFrameGasUsed;
        ulong spentGas = Math.Max(grossGas - RefundHelper.CalculateClaimableRefund(grossGas, (ulong)refundCounter, spec), floorGas);
        // Block-level gas accounting reads Transaction.BlockGasUsed, whose getter otherwise falls back
        // to tx.GasLimit (the frame-gas sum, not the gas actually spent). Set it explicitly like the
        // regular path so parallel block validation (BlockAccessListManager) accumulates the frame
        // tx's real spent gas into the header.
        tx.BlockGasUsed = spentGas;
        Address payer = frameContext.Payer;

        // The payer was charged the max cost at payment approval; refund the unused remainder and
        // pay the beneficiary premium. The base-fee share stays deducted (burned).
        // EIP-8141/EIP-4844: the blob fee is also charged and burned, excluded from the refund. Both
        // legs are bounded by max_cost (spentCost <= gas leg, blobFee is the blob leg), so no underflow.
        UInt256 spentCost = (UInt256)spentGas * effectiveGasPrice;
        UInt256 chargedCost = spentCost + blobFee;
        if (maxCost > chargedCost)
        {
            WorldState.AddToBalance(payer, maxCost - chargedCost, spec);
        }

        // EIP-4844 fee-collector chains (e.g. Gnosis) collect the burned blob fee, as in PayFees;
        // elsewhere it is burned. The base-fee share stays burned as no chain enables both features.
        if (!blobFee.IsZero && spec.IsEip4844FeeCollectorEnabled && spec.FeeCollector is not null)
        {
            WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, blobFee, spec);
        }

        // EIP-1559/EIP-7928: fee accounting touches the beneficiary regardless of premium, so the
        // credit is applied unconditionally like PayFees on the regular path; the BAL recorder drops
        // the zero balance change and the EIP-158 commit clears a touched-empty beneficiary.
        // PayFees also skips the credit for a beneficiary self-destructed within the tx (EIP-6780),
        // but the frame path never finalizes its destroy list, so there is nothing to resurrect here;
        // mirroring the guard would burn the premium while leaving the account live, matching neither
        // path. Destroy-list finalization on the frame path (and the guard it would justify) is tracked
        // separately.
        UInt256 fees = premiumPerGas * (UInt256)spentGas;
        WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec);

        bool commit = opts.HasFlag(ExecutionOptions.Commit);
        if (commit)
        {
            WorldState.Commit(spec, commitRoots: false);
        }

        if (opts.HasFlag(ExecutionOptions.Restore))
        {
            WorldState.Restore(txSnapshot);
        }

        if (tracer.IsTracingFees)
        {
            // As in PayFees, the burnt half is capped at the effective price paid so validation-off
            // runs with max fee below base fee do not over-report; the burned blob fee is added in.
            UInt256 effectiveBaseFee = UInt256.Min(header.BaseFeePerGas, effectiveGasPrice);
            tracer.ReportFees(fees, effectiveBaseFee * spentGas + blobFee);
        }

        if (tracer.IsTracingReceipt)
        {
            if (tracer is IFrameTxReceiptTracer frameReceiptTracer)
            {
                frameReceiptTracer.ReportFrameTxReceipt(payer, frameReceipts);
            }

            // Derive the tx log set from the per-frame receipts rather than maintaining a parallel
            // union, so the two can't diverge: an unrolled batch clears its frames' logs above.
            LogEntry[] txLogs = ConcatFrameLogs(frameReceipts);
            GasConsumed gasConsumed = new(spentGas, spentGas, spentGas);
            tracer.MarkAsSuccess(Eip8141Constants.EntryPointAddress, in gasConsumed, [], txLogs);
        }

        return TransactionResult.Ok;
    }

    /// <summary>
    /// The transaction log set: the frame-order concatenation of the per-frame receipt logs.
    /// </summary>
    private static LogEntry[] ConcatFrameLogs(TxFrameReceipt[] frameReceipts)
    {
        int total = 0;
        foreach (TxFrameReceipt frameReceipt in frameReceipts)
        {
            total += frameReceipt.Logs.Length;
        }

        if (total == 0) return [];

        LogEntry[] logs = new LogEntry[total];
        int offset = 0;
        foreach (TxFrameReceipt frameReceipt in frameReceipts)
        {
            LogEntry[] frameLogs = frameReceipt.Logs;
            frameLogs.CopyTo(logs, offset);
            offset += frameLogs.Length;
        }

        return logs;
    }

    /// <summary>
    /// FRAME_TX_INTRINSIC_COST + frames × FRAME_TX_PER_FRAME_COST + calldata cost of frame data
    /// and signature fields + per-scheme signature verification cost.
    /// </summary>
    /// <remarks>
    /// The rate and token weighting behind <paramref name="floorGas"/> are resolved from the spec, as
    /// <see cref="IntrinsicGasCalculator.CalculateFloorCost"/> does, so a frame transaction's floor
    /// cannot diverge from an ordinary transaction's under the same fork.
    /// </remarks>
    /// <param name="floorGas">The minimum chargeable gas, or 0 when floor pricing is not active.</param>
    private static ulong CalculateFrameTxIntrinsicGas(Transaction tx, TxFrame[] frames, IReleaseSpec spec, out ulong floorGas)
    {
        ulong tokens = 0;
        ulong dataLength = 0;
        foreach (TxFrame frame in frames)
        {
            tokens += CountCalldataTokens(frame.Data.Span, spec);
            dataLength += (ulong)frame.Data.Length;
        }

        ulong signatureVerificationCost = 0;
        TxFrameSignature[]? signatures = tx.FrameSignatures;
        if (signatures is not null)
        {
            foreach (TxFrameSignature signature in signatures)
            {
                tokens += signature.Signer is null ? 0 : CountCalldataTokens(signature.Signer.Bytes, spec);
                tokens += CountCalldataTokens(signature.Msg.Span, spec);
                tokens += CountCalldataTokens(signature.Signature.Span, spec);
                dataLength += (ulong)(signature.Signer is null ? 0 : Address.Size)
                              + (ulong)signature.Msg.Length
                              + (ulong)signature.Signature.Length;
                signatureVerificationCost += signature.Scheme switch
                {
                    TxFrameSignature.SchemeArbitrary => Eip8141Constants.ArbitraryVerificationGasCost,
                    TxFrameSignature.SchemeSecp256k1 => Eip8141Constants.Secp256k1VerificationGasCost,
                    TxFrameSignature.SchemeP256 => Eip8141Constants.P256VerificationGasCost,
                    _ => 0,
                };
            }
        }

        ulong mandatoryGas = (ulong)Eip8141Constants.IntrinsicGasCost
                             + (ulong)frames.Length * (ulong)Eip8141Constants.PerFrameGasCost
                             + signatureVerificationCost;
        ulong floorTokens = spec.IsEip7976Enabled ? dataLength * spec.GasCosts.TxDataNonZeroMultiplier : tokens;
        floorGas = spec.IsEip7623Enabled ? mandatoryGas + floorTokens * spec.GasCosts.TotalCostFloorPerToken : 0;
        return mandatoryGas + tokens * GasCostOf.TxDataZero;
    }

    private static ulong CountCalldataTokens(ReadOnlySpan<byte> data, IReleaseSpec spec)
    {
        int zeros = data.CountZeros();
        return (ulong)zeros + (ulong)(data.Length - zeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }

    private TransactionSubstate ExecuteFrame(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, in StackAccessTracker accessTracker, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed)
    {
        // As with an ordinary CALL, a caller unable to fund the value transfer reverts the frame.
        UInt256 value = frame.Value;
        if (!value.IsZero && WorldState.GetBalance(caller) < value)
        {
            gasUsed = 0;
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        // Default code: a codeless target (empty code hash, no EIP-7702 delegation indicator) runs
        // the protocol-defined behavior instead of the EVM.
        if (WorldState.GetCodeHash(resolvedTarget) == Keccak.OfAnEmptyString)
        {
            return ExecuteDefaultCode(frame, resolvedTarget, caller, isStatic, frameContext, in accessTracker, spec, tracer, out gasUsed);
        }

        CodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(resolvedTarget, spec, out _);
        ReadOnlyMemory<byte> inputData = frame.Data;

        ExecutionEnvironment env = ExecutionEnvironment.Rent(
            codeInfo: codeInfo,
            executingAccount: resolvedTarget,
            caller: caller,
            codeSource: resolvedTarget,
            callDepth: 0,
            value: in value,
            inputData: in inputData);

        Snapshot snapshot = WorldState.TakeSnapshot();
        if (!value.IsZero)
        {
            // The VM credits the executing account; the caller-side debit is the processor's job.
            WorldState.SubtractFromBalance(caller, in value, spec);
        }

        // Shared journal: snapshot before warming the frame's target so a reverting frame also
        // reverts its warm/cold touches (spec: "If a frame reverts, warm / cold status reverts to
        // the state before the frame").
        StackAccessTracker frameTracker = accessTracker;
        frameTracker.TakeSnapshot();
        if (spec.UseHotAndColdStorage)
        {
            frameTracker.WarmUp(resolvedTarget);
        }

        using VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel(
            TGasPolicy.FromULong(frame.GasLimit),
            isStatic ? ExecutionType.STATICCALL : ExecutionType.TRANSACTION,
            env,
            in frameTracker,
            in snapshot,
            isStatic: isStatic);

        TransactionSubstate substate = VirtualMachine.ExecuteTransaction(state, WorldState, tracer);

        ulong remainingGas = substate.IsError ? 0 : TGasPolicy.GetRemainingGas(in state.Gas);
        gasUsed = frame.GasLimit - remainingGas;

        if (substate.ShouldRevert || substate.IsError)
        {
            WorldState.Restore(snapshot);
            frameTracker.Restore();
        }

        return substate;
    }

    /// <summary>
    /// EIP-8141 default code for a codeless target. VERIFY: require a canonical-hash SECP256K1
    /// signature at index 0 whose resolved signer is the target, then signal APPROVE with the
    /// frame's allowed scope. SENDER/DEFAULT: succeed as empty code, performing the value transfer
    /// and emitting its EIP-7708 transfer log into the frame receipt.
    /// The signature's cryptographic validity is already checked in pre-flight; default code checks
    /// only the structural conditions the spec pins.
    /// EIP8141-ISSUE: the spec reads the signature from the hoisted <c>signatures</c> list at index
    /// 0; the ethrex public devnet carries it in the VERIFY frame's data instead — an open
    /// cross-client divergence to raise upstream. This follows the spec (hoisted list).
    /// EIP8141: default-code gas metering is pending, so the transfer to a dead recipient always
    /// logs (the EIP-8037 NEW_ACCOUNT charge that suppresses it on the VM path never applies) and
    /// needs no frame-journal snapshot. A gas charge here would revisit both.
    /// </summary>
    /// <param name="accessTracker">Cross-frame log/warm journal; the transfer log is appended to
    /// its log list so it reaches the frame receipt and the transaction log union.</param>
    private TransactionSubstate ExecuteDefaultCode(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, in StackAccessTracker accessTracker, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed)
    {
        gasUsed = 0;

        if (isStatic)
        {
            byte allowedScope = frame.AllowedApproveScope;
            if (allowedScope == 0)
            {
                return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
            }

            // The expected signature index depends on the frame's scope: execution (or both) reads
            // index 0; a payment-only verifier (a codeless EOA sponsor) reads index 1
            // (ethereum/EIPs#11954).
            int sigIndex = (allowedScope & TxFrame.ApproveExecution) != 0 ? 0 : 1;
            TxFrameSignature[] signatures = frameContext.Signatures;
            if (signatures.Length <= sigIndex
                || signatures[sigIndex].Scheme != TxFrameSignature.SchemeSecp256k1
                || !signatures[sigIndex].Msg.IsEmpty
                || frameContext.ResolvedSigner(sigIndex) != resolvedTarget)
            {
                return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
            }

            frameContext.ApprovalScopeSignal = allowedScope;
            return DefaultCodeSuccess();
        }

        // SENDER / DEFAULT: as if calling empty code — perform only the value transfer.
        UInt256 value = frame.Value;
        if (!value.IsZero)
        {
            WorldState.SubtractFromBalance(caller, in value, spec);
            WorldState.AddToBalanceAndCreateIfNotExists(resolvedTarget, in value, spec);
            // EIP-7708: self-transfers emit no log; zero value is excluded by the outer guard.
            if (spec.IsEip7708Enabled && caller != resolvedTarget)
            {
                LogEntry transferLog = TransferLog.CreateTransfer(caller, resolvedTarget, in value);
                accessTracker.Logs.Add(transferLog);
                if (tracer.IsTracingLogs) tracer.ReportLog(transferLog);
            }
        }

        return DefaultCodeSuccess();
    }

    private static TransactionSubstate DefaultCodeSuccess() =>
        new(ReadOnlyMemory<byte>.Empty, refund: 0, destroyList: null, logs: null, shouldRevert: false);

    private void ApplyApproval(FrameTxContext frameContext, Address resolvedTarget, IReleaseSpec spec)
    {
        // Approval validity (scope allowance, re-approval, target, prior execution approval, payer
        // balance) is enforced by the APPROVE handler, which reverts the frame on violation; the
        // outer loop only forwards signals of successfully completed frames.
        byte scope = frameContext.ApprovalScopeSignal;
        if (scope == 0) return;
        frameContext.ApprovalScopeSignal = 0;

        if ((scope & TxFrame.ApproveExecution) != 0)
        {
            frameContext.SenderApproved = true;
        }

        if ((scope & TxFrame.ApprovePayment) != 0)
        {
            // The APPROVE opcode rejects a second payer and payment before execution approval
            // (EvmInstructions.FrameTx.cs), but the default-code sponsor path signals approval
            // directly, bypassing those guards — so they must be re-enforced here for both paths to
            // agree. Without this, two payment approvals against the same target charge MaxCost and
            // increment the nonce twice while only the last payer is refunded.
            if (frameContext.Payer is not null || !frameContext.SenderApproved) return;

            // Re-checked at charge time: the frame may have moved the payer's balance after an
            // APPROVE issued from an inner call, and the debit must never throw mid-block. A void
            // payment leaves Payer unset, so the transaction fails the payer gate unless a later
            // frame approves payment.
            if (WorldState.GetBalance(resolvedTarget) < frameContext.MaxCost) return;

            // Charge the max cost up front from the payer and consume the sender nonce; unused
            // gas is refunded to the payer at the end of the transaction.
            WorldState.SubtractFromBalance(resolvedTarget, frameContext.MaxCost, spec);
            WorldState.IncrementNonce(frameContext.Sender);
            frameContext.Payer = resolvedTarget;
        }
    }
}
