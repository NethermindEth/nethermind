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
    /// replayable. A malformed set is rejected as malformed, not as a nonce mismatch: it names no sequence.
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

        // Cold path only: re-read to report the same too-low / too-high distinction as the account nonce.
        if (!KeyedNonceManager.AreNonceKeysWellFormed(nonceKeys))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction nonce key set is not well-formed");
        }

        if (tx.Nonce >= Eip8250Constants.MaxNonceSeq)
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction nonce sequence is exhausted");
        }

        ulong current = KeyedNonceManager.CurrentNonceSeq(WorldState, sender, nonceKeys[0]);
        return (tx.Nonce < current
            ? TransactionResult.ErrorType.TransactionNonceTooLow
            : TransactionResult.ErrorType.TransactionNonceTooHigh).WithDetail("frame transaction nonce sequence mismatch");
    }

    private TransactionResult ExecuteFrameTx(Transaction tx, ITxTracer tracer, ExecutionOptions opts, BlockHeader header, IReleaseSpec spec)
    {
        Address sender = tx.SenderAddress!;
        Snapshot txSnapshot = WorldState.TakeSnapshot();

        if (tx.NonceKeys is not null && !spec.IsEip8250Enabled)
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("keyed nonces are not enabled");
        }

        // Pre-flight: nonce and protocol-validated signatures.
        TransactionResult nonceResult = ValidateFrameTxNonce(tx, sender);
        if (!nonceResult) return nonceResult;

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
        // EIP-8037: net committed state gas across frames, for the block's state dimension.
        long totalStateGas = 0;
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

        Snapshot prefixEndSnapshot = txSnapshot;
        int prefixEndIndex = -1;
        long prefixEndRefund = 0;
        long prefixEndStateGas = 0;
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
                batchStartStateGas = totalStateGas;
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
            TransactionSubstate substate = ExecuteFrame(frame, resolvedTarget, caller, isStatic, frameContext, in accessTracker, spec, tracer, out ulong frameGasUsed, out long frameStateGas);
            totalFrameGasUsed += frameGasUsed;
            totalStateGas += frameStateGas;

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

            if (frame.Mode == TxFrame.ModePostTx && !frameSucceeded)
            {
                // A failed assertion discards the body down to the validation prefix and overrides any
                // atomic-batch unrolling, but unlike a VERIFY revert it leaves the transaction valid.
                WorldState.Restore(prefixEndSnapshot);
                refundCounter = prefixEndRefund;
                // The discarded body commits no state, so its state gas leaves the block dimension
                // while the execution gas it consumed stays charged (as in unroll_atomic_batch).
                totalStateGas = prefixEndStateGas;

                // Body logs go with the state that produced them; the tx log set is derived from these
                // receipts, so clearing them here also keeps them out of the bloom.
                for (int s = prefixEndIndex + 1; s < i; s++)
                {
                    TxFrameReceipt reverted = frameReceipts[s];
                    if (reverted.Logs.Length > 0)
                    {
                        frameReceipts[s] = new TxFrameReceipt(reverted.Status, reverted.GasUsed, []);
                    }
                }

                for (int s = i + 1; s < frames.Length; s++)
                {
                    frameReceipts[s] = new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, []);
                    frameContext.MarkFrameSkipped(s);
                }

                postTxReverted = true;
                break;
            }

            if (frameSucceeded)
            {
                bool payerWasSet = frameContext.Payer is not null;
                ApplyApproval(frameContext, resolvedTarget, spec);
                if (!payerWasSet && frameContext.Payer is not null)
                {
                    // End of the validation prefix (the shortest prefix whose success sets the payer):
                    // EIP-7906 keeps everything up to here and discards the execution body when a
                    // POST_TX frame reverts.
                    prefixEndSnapshot = WorldState.TakeSnapshot();
                    prefixEndIndex = i;
                    prefixEndRefund = refundCounter;
                    prefixEndStateGas = totalStateGas;
                }
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

                    // Refunds from the reverted batch are discarded with its state, so roll the counter back.
                    // No payer/sender_approved rollback is needed: EIP-8141 forbids approval scope on batch frames.
                    refundCounter = batchStartRefund;
                    // unroll_atomic_batch: the batch commits no state, so its state gas leaves the block
                    // dimension while the execution gas it consumed stays charged.
                    totalStateGas = batchStartStateGas;

                    if (prefixEndIndex >= batchStartIndex)
                    {
                        // The prefix ended inside the batch, so its snapshot now points past the
                        // truncated journal and unwinding a failed assertion to it would throw.
                        prefixEndSnapshot = batchStartSnapshot;
                        prefixEndIndex = batchStartIndex - 1;
                        prefixEndRefund = batchStartRefund;
                        prefixEndStateGas = batchStartStateGas;
                    }

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
        // settle_transaction_gas: EIP-8037 charges the block max(execution, state), so the gross sum is
        // split into the two dimensions; without it the block keeps a single pre-refund dimension.
        ulong blockStateGas = 0;
        ulong blockExecutionGas;
        if (spec.IsEip8037Enabled)
        {
            blockStateGas = (ulong)Math.Max(0, totalStateGas);
            blockExecutionGas = Eip8037BlockGasInclusionCheck.CalculateBlockExecutionGas(grossGas, blockStateGas, floorGas);
        }
        else
        {
            blockExecutionGas = spec.IsEip7778Enabled ? Math.Max(grossGas, floorGas) : 0;
        }

        GasConsumed gasConsumed = new(SpentGas: spentGas, OperationGas: spentGas, BlockGas: blockExecutionGas, BlockStateGas: blockStateGas);
        tx.BlockGasUsed = gasConsumed.EffectiveBlockGas;
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

            // Derive the tx log set from the per-frame receipts rather than maintaining a parallel
            // union, so the two can't diverge: an unrolled batch clears its frames' logs above.
            LogEntry[] txLogs = TxFrameReceipt.ConcatLogs(frameReceipts);
            if (postTxReverted)
            {
                tracer.MarkAsFailed(Eip8141Constants.EntryPointAddress, in gasConsumed, [], "POST_TX frame reverted");
            }
            else
            {
                tracer.MarkAsSuccess(Eip8141Constants.EntryPointAddress, in gasConsumed, [], txLogs);
            }
        }

        return TransactionResult.Ok;
    }

    private TransactionSubstate ExecuteFrame(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, in StackAccessTracker accessTracker, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed, out long stateGasUsed)
    {
        stateGasUsed = 0;
        gasUsed = 0;
        // As with an ordinary CALL, a caller unable to fund the value transfer reverts the frame.
        UInt256 value = frame.Value;
        if (!value.IsZero && WorldState.GetBalance(caller) < value)
        {
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        bool codeless = WorldState.GetCodeHash(resolvedTarget) == Keccak.OfAnEmptyString;

        // The spec places VERIFY default code before the frame's EVM entry, so it pays no entry charge.
        if (codeless && frame.Mode == TxFrame.ModeVerify)
        {
            return ExecuteDefaultVerify(frame, resolvedTarget, frameContext, tracer, out gasUsed);
        }

        // create_evm_from_frame: the frame pays target access plus EIP-8037 NEW_ACCOUNT (value transfer
        // reviving a dead target) from its own gas limit; a charge it cannot afford fails the frame.
        // EIP-2929 seeds the accessed set with every precompile, which the tracker does not hold.
        ulong entryExecution = spec.UseHotAndColdStorage
            ? (accessTracker.IsCold(resolvedTarget) && !spec.IsPrecompile(resolvedTarget) ? TGasPolicy.GetColdAccountAccessCost(spec) : Eip8038Constants.WarmAccess)
            : 0;
        long entryState = spec.IsEip8037Enabled && !value.IsZero && WorldState.IsDeadAccount(resolvedTarget)
            ? TGasPolicy.GetNewAccountStateCost()
            : 0;
        ulong entryCharge = entryExecution + (ulong)entryState;
        if (entryCharge > frame.GasLimit)
        {
            gasUsed = frame.GasLimit;
            return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
        }

        if (codeless)
        {
            // SENDER / DEFAULT default code behaves as empty code: entry charge and value transfer only.
            if (spec.UseHotAndColdStorage) accessTracker.WarmUp(resolvedTarget);
            gasUsed = entryCharge;
            stateGasUsed = entryState;
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

        // The reservoir starts empty, so the VM cannot draw the entry state gas back out of it.
        using VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel(
            TGasPolicy.FromULong(frame.GasLimit - entryCharge),
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

        ulong remainingGas = substate.IsError ? 0 : TGasPolicy.GetRemainingGas(in state.Gas);
        gasUsed = frame.GasLimit - remainingGas;
        // A reverted or halted frame commits no state, so it contributes no state gas.
        stateGasUsed = substate.ShouldRevert || substate.IsError ? 0 : entryState + TGasPolicy.GetStateGasUsed(in state.Gas);

        if (substate.ShouldRevert || substate.IsError)
        {
            WorldState.Restore(snapshot);
            frameTracker.Restore();
        }

        return substate;
    }

    /// <summary>
    /// EIP-8141 default code for a codeless VERIFY target: approve the frame's allowed scope if a
    /// canonical-hash SECP256K1 signature resolves to the target.
    /// </summary>
    /// <remarks>
    /// The signature's cryptographic validity is already checked in pre-flight; only the structural
    /// conditions the spec pins are checked here. The only gas it can consume is the EIP-8250
    /// keyed-nonce first-use surcharge that the APPROVE opcode would otherwise have charged.
    /// EIP8141-ISSUE: the signature is read from the hoisted <c>signatures</c> list as the spec
    /// requires; some public devnet payloads carry it in the VERIFY frame's data instead.
    /// </remarks>
    private TransactionSubstate ExecuteDefaultVerify(TxFrame frame, Address resolvedTarget, FrameTxContext frameContext, ITxTracer tracer, out ulong gasUsed)
    {
        gasUsed = 0;
        byte allowedScope = frame.AllowedApproveScope;
        if (allowedScope == 0)
        {
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        // Execution (or both) scope reads signature 0; a payment-only verifier reads signature 1
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

        // APPROVE only reaches the surcharge past these same guards, and ApplyApproval discards the
        // approval for the same reasons, so charging ahead of them would bill a consumption that never happens.
        if ((allowedScope & TxFrame.ApprovePayment) != 0
            && frameContext.Payer is null
            && ((allowedScope & TxFrame.ApproveExecution) != 0 || frameContext.SenderApproved)
            && WorldState.GetBalance(resolvedTarget) >= frameContext.MaxCost
            && frameContext.NonceKeys is { } nonceKeys)
        {
            ulong surcharge = KeyedNonceManager.FirstUseSurcharge(WorldState, frameContext.Sender, nonceKeys);
            if (surcharge > frame.GasLimit)
            {
                // An error frame reports its whole gas limit on the EVM path; halting for free
                // here would price the same failure differently.
                gasUsed = frame.GasLimit;
                return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
            }

            gasUsed = surcharge;
        }

        frameContext.ApprovalScopeSignal = allowedScope;
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

            // EIP-8250: the account nonce cannot advance past MAX_NONCE_SEQ, and an approval that
            // cannot consume its nonce performs no approval effects at all.
            UInt256[]? keys = frameContext.NonceKeys;
            if ((keys is null || (keys.Length == 1 && keys[0].IsZero))
                && WorldState.GetNonce(frameContext.Sender) >= Eip8250Constants.MaxNonceSeq)
            {
                return;
            }

            // Charge the max cost up front from the payer and consume the sender nonce; unused
            // gas is refunded to the payer at the end of the transaction.
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
        }
    }
}
