// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// EIP-8141 frame transaction outer loop. Runs pre-flight (nonce + signature validation), then each
/// frame as its own EVM call under the frame-transaction execution context, applying APPROVE effects,
/// enforcing the payer gate, charging spec gas, and producing per-frame receipts.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public abstract partial class TransactionProcessorBase<TGasPolicy>
{
    /// <summary>Checks a frame transaction's nonce against the sender's state, under either nonce shape.</summary>
    /// <remarks>
    /// With <see cref="Transaction.NonceKeys"/> every selected key must currently sit at
    /// <see cref="Transaction.Nonce"/>, so the set is consumed as a unit and a partially advanced set is not
    /// replayable. Assumes the caller has already checked well-formedness, which is not state-dependent and
    /// so is enforced whether or not the entry point validates.
    /// </remarks>
    private TransactionResult ValidateFrameTxNonce(Transaction tx, Address sender)
    {
        UInt256[]? nonceKeys = tx.NonceKeys;
        if (nonceKeys is null)
        {
            UInt256 accountNonce = WorldState.GetNonce(sender);
            return accountNonce == tx.Nonce
                ? TransactionResult.Ok
                : (tx.Nonce < accountNonce
                    ? TransactionResult.ErrorType.TransactionNonceTooLow
                    : TransactionResult.ErrorType.TransactionNonceTooHigh).WithDetail("frame transaction nonce mismatch");
        }

        if (KeyedNonceManager.IsNonceSetValid(WorldState, sender, nonceKeys, tx.Nonce))
        {
            return TransactionResult.Ok;
        }

        if (tx.Nonce >= Eip8250Constants.MaxNonceSeq)
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction nonce sequence is exhausted");
        }

        // Cold path only: re-read to report the same too-low / too-high distinction as the account nonce.
        ulong current = KeyedNonceManager.CurrentNonceSeq(WorldState, sender, nonceKeys[0]);
        return (tx.Nonce < current
            ? TransactionResult.ErrorType.TransactionNonceTooLow
            : TransactionResult.ErrorType.TransactionNonceTooHigh).WithDetail("frame transaction nonce sequence mismatch");
    }

    private TransactionResult ExecuteFrameTx(Transaction tx, ITxTracer tracer, ExecutionOptions opts, BlockHeader header, IReleaseSpec spec)
    {
        if (opts.HasFlag(ExecutionOptions.FrameValidationPrefixOnly))
        {
            return SimulateFrameValidationPrefix(tx, tracer, header, spec);
        }

        Address sender = tx.SenderAddress!;
        Snapshot txSnapshot = WorldState.TakeSnapshot();

        if (tx.NonceKeys is not null && !spec.IsEip8250Enabled)
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("keyed nonces are not enabled");
        }

        // Structural, so it holds even where validation is skipped: the fixed-size buffers reached below
        // take a well-formed set as their precondition, and eth_call arrives here without a validator.
        if (tx.NonceKeys is { } nonceKeys && !KeyedNonceManager.AreNonceKeysWellFormed(nonceKeys))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction nonce key set is not well-formed");
        }

        // Pre-flight: nonce and protocol-validated signatures. The state comparison follows SkipValidation
        // as the account-nonce path does: eth_call overwrites the supplied nonce with the account nonce.
        if (ShouldValidate(opts))
        {
            TransactionResult nonceResult = ValidateFrameTxNonce(tx, sender);
            if (!nonceResult) return nonceResult;
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

        // Enforced here, not only in static validation, so unvalidated entry points (e.g. eth_call)
        // cannot mint ETH: EIP-8141 forbids approval scope on a frame belonging to an atomic batch.
        bool prevIsAtomicBatch = false;
        foreach (TxFrame frame in frames)
        {
            if ((frame.IsAtomicBatch || prevIsAtomicBatch) && frame.AllowedApproveScope != 0)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("approval scope on atomic batch frame");
            }

            // An undefined mode would otherwise fall through to DEFAULT semantics and execute.
            if (frame.Mode > TxFrame.ModePostTx)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail(FrameTxValidation.InvalidMode);
            }

            // The mode is undefined until EIP-7906 defines it, so it must not run with assertion semantics.
            if (frame.Mode == TxFrame.ModePostTx && !spec.IsEip7906Enabled)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail(FrameTxValidation.PostTxNotEnabled);
            }

            prevIsAtomicBatch = frame.IsAtomicBatch;
        }

        if (tx.RecentRootReferences is { } references)
        {
            if (!spec.IsEip8272Enabled)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("recent root references are not enabled");
            }

            if (references.Length > Eip8272Constants.MaxRecentRootReferences)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("too many recent root references");
            }
        }

        if (tx.NonceKeys is not null)
        {
            tx.FrameCalldataStats = FrameTxNonceCalldata.Measure(tx);
        }

        // The frame gas sum is overflow-checked so the processor does not depend on static validation
        // having run.
        tx.ReferenceCalldataStats = RecentRootReferenceDecoder.Instance.Measure(tx.RecentRootReferences);
        if (!FrameTxValidation.TryCalculateGasBudget(tx, spec, out ulong intrinsicGas, out ulong floorGas, out ulong txGasLimit))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction gas limit overflows");
        }

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
            tx.MaxFeePerBlobGas.GetValueOrDefault(),
            WorldState.GetNonce(sender),
            tx.RecentRootReferences,
            tx.NonceKeys);

        TxFrameReceipt[] frameReceipts = new TxFrameReceipt[frames.Length];
        ulong totalFrameGasUsed = 0;
        long totalFrameStateGasUsed = 0;
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

        if (!RecentRootReferences.Validate(WorldState, tx.RecentRootReferences, header.SlotNumber, in accessTracker))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("recent root reference is not committed or out of range");
        }

        // Atomic batch state: a maximal contiguous run [i, j] where i..j-1 have ATOMIC_BATCH_FLAG
        // and j does not. On any failure inside the run, state rolls back to before the run began
        // and the remaining frames are skipped (spec: Behavior, atomic batch).
        bool inBatch = false;
        Snapshot batchStartSnapshot = default;
        StackAccessTracker batchTracker = default;
        int batchStartIndex = 0;
        long batchStartRefund = 0;
        long batchStartStateGas = 0;
        int batchStartJournal = 0;

        Snapshot prefixEndSnapshot = txSnapshot;
        int prefixEndIndex = -1;
        long prefixEndRefund = 0;
        long prefixEndStateGas = 0;
        int prefixEndJournal = 0;
        bool postTxReverted = false;

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
                batchStartRefund = refundCounter;
                batchStartStateGas = totalFrameStateGasUsed;
                batchStartJournal = frameContext.StateGasJournalCheckpoint;
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
            bool isStatic = frame.Mode is TxFrame.ModeVerify or TxFrame.ModePostTx;

            // ORIGIN returns the frame's caller throughout all call depths.
            VirtualMachine.SetTxExecutionContext(new TxExecutionContext(
                caller, _codeInfoRepository, tx.BlobVersionedHashes, in effectiveGasPrice, frameContext));

            // The shared journal accumulates logs across frames; this frame's own logs start here.
            int frameLogStart = accessTracker.Logs.Count;
            int frameStartJournal = frameContext.StateGasJournalCheckpoint;
            bool payerWasSet = frameContext.Payer is not null;
            TransactionSubstate substate = ExecuteFrame(frame, resolvedTarget, caller, isStatic, frameContext, in accessTracker, spec, tracer, out ulong frameGasUsed, out long frameStateGas);

            bool frameSucceeded = !substate.ShouldRevert && !substate.IsError;
            if (frameSucceeded && frameContext.ApprovalScopeSignal != 0)
            {
                long remainingStateGas = (frame.StateGasLimit > long.MaxValue ? long.MaxValue : (long)frame.StateGasLimit) - frameStateGas;
                if (!TryApplyApproval(frameContext, resolvedTarget, spec, in accessTracker, remainingStateGas, out long approvalStateGas))
                {
                    frameSucceeded = false;
                    substate = new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
                    frameGasUsed = frame.ExecutionGasLimit;
                    frameStateGas = 0;
                }
                else
                {
                    frameGasUsed += (ulong)approvalStateGas;
                    frameStateGas += approvalStateGas;
                }
            }
            else if (!frameSucceeded)
            {
                frameContext.ApprovalScopeSignal = 0;
            }

            totalFrameGasUsed += frameGasUsed;
            if (frameSucceeded)
            {
                totalFrameStateGasUsed += frameStateGas;
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
            ulong frameStateGasUsed = frameSucceeded ? (ulong)frameStateGas : 0;
            frameReceipts[i] = new TxFrameReceipt(
                frameSucceeded ? TxFrameReceipt.StatusSuccess : TxFrameReceipt.StatusFailure,
                frameGasUsed - frameStateGasUsed,
                frameStateGasUsed,
                frameLogs);
            frameContext.RecordFrameReceipt(i, frameGasUsed - frameStateGasUsed, frameStateGasUsed);

            if (frame.Mode == TxFrame.ModeVerify && !frameSucceeded)
            {
                // A failed VERIFY frame invalidates the whole transaction.
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("VERIFY frame reverted");
            }

            if (frame.Mode == TxFrame.ModePostTx && !frameSucceeded)
            {
                // A failed assertion discards the body down to the validation prefix and overrides any
                // atomic-batch unrolling, but unlike a VERIFY revert it leaves the transaction valid.
                WorldState.Restore(prefixEndSnapshot);
                refundCounter = prefixEndRefund;

                // Body logs go with the state that produced them; the tx log set is derived from these
                // receipts, so clearing them here also keeps them out of the bloom.
                for (int s = prefixEndIndex + 1; s < i; s++)
                {
                    TxFrameReceipt reverted = frameReceipts[s];
                    if (reverted.Logs.Length > 0 || reverted.StateGasUsed > 0)
                    {
                        frameReceipts[s] = new TxFrameReceipt(reverted.Status, reverted.ExecutionGasUsed, 0, []);
                        frameContext.ClearFrameStateGasUsed(s);
                    }
                }

                totalFrameGasUsed -= (ulong)(totalFrameStateGasUsed - prefixEndStateGas);
                totalFrameStateGasUsed = prefixEndStateGas;
                frameContext.RestoreStateGasJournal(prefixEndJournal);

                for (int s = i + 1; s < frames.Length; s++)
                {
                    frameReceipts[s] = new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, []);
                    frameContext.MarkFrameSkipped(s);
                }

                postTxReverted = true;
                break;
            }

            if (frameSucceeded)
            {
                if (!payerWasSet && frameContext.Payer is not null)
                {
                    // End of the validation prefix (the shortest prefix whose success sets the payer):
                    // EIP-7906 keeps everything up to here and discards the execution body when a
                    // POST_TX frame reverts.
                    prefixEndSnapshot = WorldState.TakeSnapshot();
                    prefixEndIndex = i;
                    prefixEndRefund = refundCounter;
                    prefixEndStateGas = totalFrameStateGasUsed;
                    prefixEndJournal = frameContext.StateGasJournalCheckpoint;
                }
            }
            else if (!inBatch)
            {
                frameContext.RestoreStateGasJournal(frameStartJournal);
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
                        if (earlier.Logs.Length > 0 || earlier.StateGasUsed > 0)
                        {
                            frameReceipts[s] = new TxFrameReceipt(earlier.Status, earlier.ExecutionGasUsed, 0, []);
                            frameContext.ClearFrameStateGasUsed(s);
                        }
                    }

                    // The unrolled frames' writes are gone with the snapshot, so their state charges
                    // are not owed either; the counter only grows, so the batch-start value undoes them.
                    totalFrameGasUsed -= (ulong)(totalFrameStateGasUsed - batchStartStateGas);
                    totalFrameStateGasUsed = batchStartStateGas;
                    frameContext.RestoreStateGasJournal(batchStartJournal);
                    // Refunds from the reverted batch are discarded with its state, so roll the counter back.
                    // No payer/sender_approved rollback is needed: EIP-8141 forbids approval scope on batch frames.
                    refundCounter = batchStartRefund;

                    if (prefixEndIndex >= batchStartIndex)
                    {
                        // The prefix ended inside the batch, so its snapshot now points past the
                        // truncated journal and unwinding a failed assertion to it would throw.
                        prefixEndSnapshot = batchStartSnapshot;
                        prefixEndIndex = batchStartIndex - 1;
                        prefixEndRefund = batchStartRefund;
                        prefixEndStateGas = batchStartStateGas;
                        prefixEndJournal = batchStartJournal;
                    }

                    int terminal = i;
                    while (terminal < frames.Length && frames[terminal].IsAtomicBatch) terminal++;
                    for (int s = i + 1; s <= terminal && s < frames.Length; s++)
                    {
                        frameReceipts[s] = new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, []);
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
        long stateGasCorrection = 0;
        for (int f = 0; f < frameReceipts.Length; f++)
        {
            long correction = frameContext.StateGasCorrectionFor(f);
            if (correction > 0)
            {
                stateGasCorrection += correction;
                TxFrameReceipt corrected = frameReceipts[f];
                ulong reducedState = corrected.StateGasUsed > (ulong)correction ? corrected.StateGasUsed - (ulong)correction : 0;
                frameReceipts[f] = new TxFrameReceipt(corrected.Status, corrected.ExecutionGasUsed, reducedState, corrected.Logs);
            }
        }

        ulong grossGasBeforeCorrection = intrinsicGas + totalFrameGasUsed;
        ulong stateGasCorrectionApplied = (ulong)Math.Max(0, stateGasCorrection);
        ulong grossGas = grossGasBeforeCorrection > stateGasCorrectionApplied ? grossGasBeforeCorrection - stateGasCorrectionApplied : 0;
        ulong gasAfterRefund = grossGas - RefundHelper.CalculateClaimableRefund(grossGas, (ulong)refundCounter, spec);
        ulong blockStateGas = (ulong)Math.Max(0, totalFrameStateGasUsed - stateGasCorrection);
        ulong blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockExecutionGas(gasAfterRefund, blockStateGas, floorGas);
        ulong spentGas = blockRegularGas + blockStateGas;
        // Block-level gas accounting reads Transaction.BlockGasUsed, whose getter otherwise falls back
        // to tx.GasLimit (the frame-gas sum, not the gas actually spent). Set it explicitly like the
        // regular path so parallel block validation (BlockAccessListManager) accumulates the frame
        // tx's real regular gas into the header.
        tx.BlockGasUsed = blockRegularGas;
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

        // Fee-collector chains (e.g. Gnosis) collect the otherwise-burned legs, exactly as PayFees does:
        // the EIP-1559 base-fee share and, when EIP-4844 fee collection is enabled, the blob fee.
        UInt256 effectiveBaseFee = UInt256.Min(header.BaseFeePerGas, effectiveGasPrice);
        UInt256 collectedFees = spec.IsEip1559Enabled ? effectiveBaseFee * (UInt256)spentGas : UInt256.Zero;
        if (spec.IsEip4844FeeCollectorEnabled)
        {
            collectedFees += blobFee;
        }
        if (spec.FeeCollector is not null && !collectedFees.IsZero)
        {
            WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, collectedFees, spec);
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

        // CommitAndRestore asks for both, and a commit clears the journals the snapshot indexes into, so restoring
        // after it would revert nothing. The frame path keeps the whole transaction in the journal until here, so the
        // restore alone returns the state the caller started with.
        if (opts.HasFlag(ExecutionOptions.Restore))
        {
            WorldState.Restore(txSnapshot);
        }
        else if (opts.HasFlag(ExecutionOptions.Commit))
        {
            WorldState.Commit(spec, commitRoots: false);
        }

        if (tracer.IsTracingFees)
        {
            // As in PayFees, the burnt/collected half is capped at the effective price paid so
            // validation-off runs with max fee below base fee do not over-report; blob fee added in.
            tracer.ReportFees(fees, effectiveBaseFee * spentGas + blobFee);
        }

        if (tracer.IsTracingReceipt)
        {
            if (tracer is IFrameTxReceiptTracer frameReceiptTracer)
            {
                frameReceiptTracer.ReportFrameTxReceipt(payer, frameReceipts);
            }

            GasConsumed gasConsumed = new(spentGas, spentGas, blockRegularGas, blockStateGas, spentGas);
            if (postTxReverted)
            {
                // The failed receipt rebuilds the log set from the frame receipts reported above.
                tracer.MarkAsFailed(Eip8141Constants.EntryPointAddress, in gasConsumed, [], "POST_TX frame reverted");
            }
            else
            {
                // Derive the tx log set from the per-frame receipts rather than maintaining a parallel
                // union, so the two can't diverge: an unrolled batch clears its frames' logs above.
                tracer.MarkAsSuccess(Eip8141Constants.EntryPointAddress, in gasConsumed, [], TxFrameReceipt.ConcatLogs(frameReceipts));
            }
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

    /// <summary>Simulates a frame transaction's validation prefix against read-only head state for mempool
    /// admission, resolving the payer under the <c>MAX_VERIFY_GAS</c> bound.</summary>
    /// <remarks>Nonce equality is deliberately not required: the prefix never reads the account nonce.</remarks>
    private TransactionResult SimulateFrameValidationPrefix(Transaction tx, ITxTracer tracer, BlockHeader header, IReleaseSpec spec)
    {
        Address sender = tx.SenderAddress!;
        Snapshot txSnapshot = WorldState.TakeSnapshot();
        try
        {
            using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);
            TransactionResult prepared = PrepareValidationPrefixSimulation(
                tx, header, spec, in accessTracker,
                out FrameTxContext frameContext, out UInt256 effectiveGasPrice, out ulong verifyGasUsed);
            if (!prepared)
            {
                return prepared;
            }

            TxFrame[] frames = tx.Frames!;
            for (int i = 0; i < frames.Length; i++)
            {
                TxFrame frame = frames[i];

                // EIP8141-GAP: deploy-frame carve-outs are unimplemented, so such a prefix is declined.
                if (OpensDeployPrefix(frames, i))
                {
                    return TransactionResult.ErrorType.MalformedTransaction.WithDetail("deploy frame in validation prefix is not simulated");
                }

                // EIP-8141 § Validation Prefix: the prefix is the shortest one that sets a payer, so a
                // non-VERIFY frame ends it — here without one.
                if (frame.Mode != TxFrame.ModeVerify)
                {
                    break;
                }

                // EIP-8141 forbids the atomic-batch flag on any validation-prefix frame.
                if (frame.IsAtomicBatch)
                {
                    return TransactionResult.ErrorType.MalformedTransaction.WithDetail("atomic batch flag in validation prefix");
                }

                frameContext.CurrentFrameIndex = i;
                WorldState.ResetTransient();

                TxFrame boundedFrame = CapFrameGas(frame, Eip8141Constants.MaxVerifyGas - verifyGasUsed, out bool capped);

                Address resolvedTarget = frame.Target ?? sender;
                Address caller = Eip8141Constants.EntryPointAddress;

                VirtualMachine.SetTxExecutionContext(new TxExecutionContext(
                    caller, _codeInfoRepository, tx.BlobVersionedHashes, in effectiveGasPrice, frameContext));

                TransactionSubstate substate = ExecuteFrame(boundedFrame, resolvedTarget, caller, isStatic: true, frameContext, in accessTracker, spec, tracer, out ulong frameGasUsed, out long frameStateGas);

                if (!substate.ShouldRevert && !substate.IsError && frameContext.ApprovalScopeSignal != 0)
                {
                    long remainingStateGas = (boundedFrame.StateGasLimit > long.MaxValue ? long.MaxValue : (long)boundedFrame.StateGasLimit) - frameStateGas;
                    if (!TryApplyApproval(frameContext, resolvedTarget, spec, in accessTracker, remainingStateGas, out long approvalStateGas))
                    {
                        substate = new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
                        frameGasUsed = boundedFrame.ExecutionGasLimit;
                        frameStateGas = 0;
                    }
                    else
                    {
                        frameGasUsed += (ulong)approvalStateGas;
                        frameStateGas += approvalStateGas;
                    }
                }

                verifyGasUsed += frameGasUsed - (ulong)frameStateGas;

                if (substate.ShouldRevert || substate.IsError)
                {
                    // Only a capped frame that ran out of gas (rather than explicitly reverting) proves the
                    // prefix exceeds MAX_VERIFY_GAS; an explicit revert is a within-budget rejection.
                    return capped && !substate.ShouldRevert
                        ? TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction validation prefix exceeds MAX_VERIFY_GAS")
                        : TransactionResult.ErrorType.MalformedTransaction.WithDetail("validation prefix frame reverted");
                }

                frameContext.MarkFrameSucceeded(i);

                // Simulation stops at the first payer, once its frame has completed successfully.
                if (frameContext.Payer is not null)
                {
                    if (tracer is IFrameTxReceiptTracer receiptTracer)
                    {
                        receiptTracer.ReportFrameTxReceipt(frameContext.Payer, []);
                    }

                    return TransactionResult.Ok;
                }
            }

            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction validation prefix never set a payer");
        }
        finally
        {
            WorldState.Restore(txSnapshot);
        }
    }

    /// <summary>Validates the transaction-level preconditions of a prefix simulation and builds the context
    /// its frames execute against.</summary>
    private TransactionResult PrepareValidationPrefixSimulation(
        Transaction tx,
        BlockHeader header,
        IReleaseSpec spec,
        in StackAccessTracker accessTracker,
        out FrameTxContext frameContext,
        out UInt256 effectiveGasPrice,
        out ulong verifyGasUsed)
    {
        frameContext = null!;
        effectiveGasPrice = default;
        verifyGasUsed = 0;

        Address sender = tx.SenderAddress!;
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        // As the main path does, so an unused P256 branch records no account access (EIP-7928).
        IPrecompile? p256Precompile = _codeInfoRepository.GetPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress, spec);
        if (!FrameTxSignatureValidator.Validate(tx, in sigHash, Ecdsa, p256Precompile, spec, out string? signatureError))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail(signatureError!);
        }

        // Signature-verification work counts against MAX_VERIFY_GAS.
        foreach (TxFrameSignature signature in tx.FrameSignatures ?? [])
        {
            verifyGasUsed += FrameTxValidation.SignatureVerificationGas(signature.Scheme);
        }
        if (verifyGasUsed > Eip8141Constants.MaxVerifyGas)
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction validation prefix exceeds MAX_VERIFY_GAS");
        }

        TransactionResult costed = CalculateSimulatedMaxCost(tx, spec, out UInt256 maxCost);
        if (!costed)
        {
            return costed;
        }

        effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out _);
        frameContext = new FrameTxContext(
            sender, tx.Nonce, tx.Frames!, tx.FrameSignatures ?? [], sigHash,
            in maxCost, in tx.MaxPriorityFeePerGas, tx.DecodedMaxFeePerGas, tx.MaxFeePerBlobGas.GetValueOrDefault(),
            WorldState.GetNonce(sender),
            tx.RecentRootReferences,
            tx.NonceKeys);

        if (spec.UseHotAndColdStorage)
        {
            if (spec.AddCoinbaseToTxAccessList) accessTracker.WarmUp(header.GasBeneficiary!);
            accessTracker.WarmUp(sender);
        }

        // RECENTROOTREFLOAD is legal in a VERIFY frame and reads the envelope on the strength of this
        // check, so the prefix must not run before it. Anchored to the earliest slot the transaction
        // could execute in, since a reference to the head slot is referenceable only from the next one.
        ulong? executionSlot = header.SlotNumber is { } headSlot ? headSlot + 1 : null;
        return RecentRootReferences.Validate(WorldState, tx.RecentRootReferences, executionSlot, in accessTracker)
            ? TransactionResult.Ok
            : TransactionResult.ErrorType.MalformedTransaction.WithDetail("recent root reference is not committed or out of range");
    }

    /// <summary>The <c>max_cost</c> (TXPARAM 0x06) the simulated prefix's APPROVE gate reads.</summary>
    /// <remarks>Taken from the same helper the main path escrows on, so the two cannot decide against
    /// different numbers, except that the blob leg is priced at <c>max_fee_per_blob_gas</c> rather than the
    /// blob base fee execution uses: the fee at inclusion is unknowable here, and over-reserving can only
    /// make admission stricter.</remarks>
    private static TransactionResult CalculateSimulatedMaxCost(Transaction tx, IReleaseSpec spec, out UInt256 maxCost)
    {
        maxCost = default;
        if (!FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong txGasLimit))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction gas budget overflows");
        }

        ulong blobGas = (ulong)(tx.BlobVersionedHashes?.Length ?? 0) * Eip4844Constants.GasPerBlob;
        return UInt256.MultiplyOverflow((UInt256)txGasLimit, tx.DecodedMaxFeePerGas, out maxCost)
            || UInt256.AddOverflow(maxCost, (UInt256)blobGas * tx.MaxFeePerBlobGas.GetValueOrDefault(), out maxCost)
            ? TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction max cost overflows")
            : TransactionResult.Ok;
    }

    /// <summary>Bounds a prefix frame's execution gas by what is left of <c>MAX_VERIFY_GAS</c>.</summary>
    /// <remarks>An opaque prefix's declared gas_limits are not structurally bounded, so this cap is what
    /// keeps cumulative validation work under the budget.</remarks>
    private static TxFrame CapFrameGas(TxFrame frame, ulong remainingVerifyGas, out bool capped)
    {
        capped = frame.ExecutionGasLimit > remainingVerifyGas;
        return capped
            ? new TxFrame(frame.Mode, frame.Flags, frame.Target, remainingVerifyGas, frame.StateGasLimit, frame.Value, frame.Data)
            : frame;
    }

    /// <summary>Whether frame <paramref name="i"/> is a <c>deploy</c> frame opening the validation prefix.</summary>
    /// <remarks>Positional, as RecognizedPrefixLength reaches index 1 only past an expiry-verify frame at index 0.</remarks>
    private static bool OpensDeployPrefix(TxFrame[] frames, int i) =>
        (i == 0 || (i == 1 && FrameTxValidation.IsExpiryVerifyFrame(frames[0])))
        && i + 1 < frames.Length
        && FrameTxValidation.IsDeployFrame(frames[i])
        && frames[i + 1].Mode == TxFrame.ModeVerify;

    private TransactionSubstate ExecuteFrame(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, in StackAccessTracker accessTracker, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed, out long stateGasUsed)
    {
        stateGasUsed = 0;

        // As with an ordinary CALL, a caller unable to fund the value transfer reverts the frame.
        UInt256 value = frame.Value;
        if (!value.IsZero && WorldState.GetBalance(caller) < value)
        {
            gasUsed = 0;
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        // EIP-8141: a precompile dispatches in every mode, leaving default code to a VERIFY frame's codeless
        // non-precompile target. The repository decides what is a precompile, being what dispatches the frame.
        if (frame.Mode == TxFrame.ModeVerify
            && _codeInfoRepository.GetPrecompile(resolvedTarget, spec) is null
            && WorldState.GetCodeHash(resolvedTarget) == Keccak.OfAnEmptyString)
        {
            return ExecuteDefaultVerifyCode(frame, resolvedTarget, frameContext, tracer, out gasUsed);
        }

        // create_evm_from_frame: the frame pays its target's access, plus the state cost of a value
        // transfer reviving a dead target, out of its own gas limit. Precompiles are checked explicitly
        // because EIP-2929 pre-warms them without the shared tracker holding them.
        ulong entryExecution = spec.UseHotAndColdStorage
            ? (accessTracker.IsCold(resolvedTarget) && !spec.IsPrecompile(resolvedTarget)
                ? TGasPolicy.GetColdAccountAccessCost(spec)
                : Eip8038Constants.WarmAccess)
            : 0;
        // Checked before the deadness query below, which is itself a recorded read: a frame that cannot
        // afford its target's access must leave the target untouched, as the CALL path does.
        if (entryExecution > frame.ExecutionGasLimit)
        {
            gasUsed = frame.ExecutionGasLimit;
            return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
        }

        // The revival charge has no pre-EIP-8037 form here: EIP-8141 is only composed onto specs carrying it.
        long entryState = spec.IsEip8037Enabled && !value.IsZero && WorldState.IsDeadAccount(resolvedTarget)
            ? TGasPolicy.GetNewAccountStateCost()
            : 0;
        TGasPolicy frameGas = TGasPolicy.FromFrameLimits(frame.ExecutionGasLimit, frame.StateGasLimit);
        if (!TGasPolicy.TryConsumeStateAndExecutionGas(ref frameGas, entryState, entryExecution))
        {
            gasUsed = frame.ExecutionGasLimit;
            return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
        }

        CodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(resolvedTarget, followDelegation: false, spec, out Address? delegation);
        if (delegation is not null)
        {
            // resolve_delegated_code_address: the target counts as accessed by now, so a self-designation is warm.
            if (spec.UseHotAndColdStorage)
            {
                ulong delegationAccess = delegation != resolvedTarget && accessTracker.IsCold(delegation) && !spec.IsPrecompile(delegation)
                    ? TGasPolicy.GetColdAccountAccessCost(spec)
                    : Eip8038Constants.WarmAccess;
                if (!TGasPolicy.TryConsume(ref frameGas, delegationAccess))
                {
                    gasUsed = frame.ExecutionGasLimit;
                    return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
                }
            }

            // create_evm_from_frame reads the designated code only after its access is paid for.
            // EIP-7702: a precompile must not execute via delegation.
            WorldState.AddAccountRead(delegation);
            codeInfo = spec.IsPrecompile(delegation)
                ? CodeInfo.Empty
                : _codeInfoRepository.GetCachedCodeInfoNoDelegation(delegation, spec);
        }

        ReadOnlyMemory<byte> inputData = frame.Data;

        // VmState.Dispose releases its environment only below the top level, so this one is caller-owned;
        // declared before the VmState so it returns to the pool after it (reverse declaration order).
        using ExecutionEnvironment env = ExecutionEnvironment.Rent(
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
            if (delegation is not null) frameTracker.WarmUp(delegation);
        }

        using VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel(
            frameGas,
            isStatic ? ExecutionType.STATICCALL : ExecutionType.TRANSACTION,
            env,
            in frameTracker,
            in snapshot,
            isStatic: isStatic);

        // Select the instruction-tracing specialisation explicitly: the parameterless ExecuteTransaction
        // overload hard-codes OffFlag and would drop per-instruction tracing for a frame.
        TransactionSubstate substate = tracer.IsTracingInstructions
            ? VirtualMachine.ExecuteTransaction<OnFlag>(state, WorldState, tracer)
            : VirtualMachine.ExecuteTransaction(state, WorldState, tracer);

        long stateReservoirSeed = frame.StateGasLimit > long.MaxValue ? long.MaxValue : (long)frame.StateGasLimit;
        if (substate.IsError || substate.ShouldRevert)
        {
            TGasPolicy.ResetForHalt(ref state.Gas, stateReservoirSeed, 0);
        }

        ulong combinedLimit = frame.ExecutionGasLimit + frame.StateGasLimit;
        gasUsed = substate.IsError
            ? combinedLimit - (ulong)Math.Max(0, TGasPolicy.GetStateReservoir(in state.Gas))
            : TGasPolicy.GetPreRefundGas(in state.Gas, combinedLimit);
        stateGasUsed = Math.Max(0, TGasPolicy.GetStateGasUsed(in state.Gas));

        if (!substate.ShouldRevert && !substate.IsError && frameContext.ApprovalScopeSignal != 0)
        {
            long remainingStateGas = stateReservoirSeed - stateGasUsed;
            if (!TryApplyApproval(frameContext, resolvedTarget, spec, in accessTracker, remainingStateGas, out long approvalStateGas))
            {
                substate = new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
                gasUsed = frame.ExecutionGasLimit;
                stateGasUsed = 0;
            }
            else
            {
                gasUsed += (ulong)approvalStateGas;
                stateGasUsed += approvalStateGas;
            }
        }

        if (substate.ShouldRevert || substate.IsError)
        {
            WorldState.Restore(snapshot);
            frameTracker.Restore();
        }

        return substate;
    }

    /// <summary>
    /// EIP-8141 default code of a <c>VERIFY</c> frame whose target has no code: require a canonical-hash
    /// SECP256K1 signature signed by the target, then APPROVE. No gas beyond the EIP-8250 surcharge.
    /// The signature's cryptographic validity is already checked in pre-flight; default code checks
    /// only the structural conditions the spec pins.
    /// </summary>
    private TransactionSubstate ExecuteDefaultVerifyCode(TxFrame frame, Address resolvedTarget, FrameTxContext frameContext, ITxTracer tracer, out ulong gasUsed)
    {
        gasUsed = 0;

        byte allowedScope = frame.AllowedApproveScope;
        if (allowedScope == 0)
        {
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        int sigIndex = (allowedScope & TxFrame.ApproveExecution) != 0 ? 0 : 1;
        TxFrameSignature[] signatures = frameContext.Signatures;
        if (signatures.Length <= sigIndex
            || signatures[sigIndex].Scheme != TxFrameSignature.SchemeSecp256k1
            || !signatures[sigIndex].Msg.IsEmpty
            || frameContext.ResolvedSigner(sigIndex) != resolvedTarget)
        {
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        // Owes the surcharge APPROVE charges, but only where APPROVE would charge it: the guards below
        // are the ones ApplyApproval discards on, so charging ahead of them bills a consumption that never happens.
        if ((allowedScope & TxFrame.ApprovePayment) != 0
            && frameContext.Payer is null
            && ((allowedScope & TxFrame.ApproveExecution) != 0 || frameContext.SenderApproved)
            && WorldState.GetBalance(resolvedTarget) >= frameContext.MaxCost
            && frameContext.NonceKeys is { } nonceKeys)
        {
            ulong surcharge = KeyedNonceManager.FirstUseSurcharge(WorldState, frameContext.Sender, nonceKeys);
            if (surcharge > frame.ExecutionGasLimit)
            {
                gasUsed = frame.ExecutionGasLimit;
                return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
            }

            gasUsed = surcharge;
        }

        frameContext.ApprovalScopeSignal = allowedScope;
        return DefaultCodeSuccess();
    }

    private static TransactionSubstate DefaultCodeSuccess() =>
        new(ReadOnlyMemory<byte>.Empty, refund: 0, destroyList: null, logs: null, shouldRevert: false);

    private bool TryApplyApproval(FrameTxContext frameContext, Address resolvedTarget, IReleaseSpec spec, in StackAccessTracker accessTracker, long availableStateGas, out long stateGasCharged)
    {
        stateGasCharged = 0;
        byte scope = frameContext.ApprovalScopeSignal;
        if (scope == 0) return true;
        frameContext.ApprovalScopeSignal = 0;

        bool approvesExecution = (scope & TxFrame.ApproveExecution) != 0;
        bool approvesPayment = (scope & TxFrame.ApprovePayment) != 0;
        bool applyPayment = false;
        bool usesAccountNonce = false;
        long newAccountCost = 0;

        if (approvesPayment)
        {
            UInt256[]? keys = frameContext.NonceKeys;
            usesAccountNonce = keys is null || !KeyedNonceManager.UsesKeyedDomain(keys);
            if (usesAccountNonce && WorldState.GetNonce(frameContext.Sender) >= Eip8250Constants.MaxNonceSeq)
            {
                return false;
            }

            bool executionApproved = frameContext.SenderApproved || approvesExecution;
            if (frameContext.Payer is null
                && executionApproved
                && WorldState.GetBalance(resolvedTarget) >= frameContext.MaxCost)
            {
                applyPayment = true;
                if (usesAccountNonce && !WorldState.AccountExists(frameContext.Sender))
                {
                    newAccountCost = TGasPolicy.GetNewAccountStateCost();
                    if (availableStateGas < newAccountCost) return false;
                }
            }
        }

        if (approvesExecution)
        {
            frameContext.SenderApproved = true;
        }

        if (applyPayment)
        {
            if (newAccountCost > 0)
            {
                stateGasCharged = newAccountCost;
                WorldState.CreateAccountIfNotExists(frameContext.Sender, UInt256.Zero);
            }

            WorldState.SubtractFromBalance(resolvedTarget, frameContext.MaxCost, spec);
            if (frameContext.NonceKeys is { } nonceKeys)
            {
                KeyedNonceManager.ConsumeNonceSet(WorldState, frameContext.Sender, nonceKeys, frameContext.Nonce);
            }
            else
            {
                WorldState.IncrementNonce(frameContext.Sender);
            }

            frameContext.Payer = resolvedTarget;
            if (spec.UseHotAndColdStorage) accessTracker.WarmUp(resolvedTarget);
        }

        return true;
    }
}
