// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

/// <summary>EIP-8141 frame transaction outer loop: pre-flight validation, then each frame as its own
/// EVM call, applying APPROVE effects, enforcing the payer gate and producing per-frame receipts.</summary>
public abstract partial class TransactionProcessorBase<TGasPolicy>
{
    /// <summary>EIP-7906: starts the slice the assertion opcodes read, for a POST_TX transaction on a
    /// path that is not already recording one. Returns the recorder so the caller can stop it.</summary>
    /// <remarks>Recording starts before the transaction touches state, so the prestate is its own baseline.
    /// The EIP-7928 condition mirrors block processing in both directions: without it, simulation could
    /// succeed on a chain whose blocks halt.</remarks>
    private IBlockAccessListSource? BeginPostTxDiffRecording(Transaction tx, ExecutionOptions opts, IReleaseSpec spec)
    {
        // The in-pool prefix simulation stops before the body, so no POST_TX frame ever runs under it.
        if (!spec.IsEip7906Enabled
            || !spec.BlockLevelAccessListsEnabled
            || opts.HasFlag(ExecutionOptions.FrameValidationPrefixOnly)
            || tx.Frames is not { } frames
            || WorldState is not IBlockAccessListSource { GeneratedBlockAccessList: null } recorder)
        {
            return null;
        }

        foreach (TxFrame frame in frames)
        {
            if (frame.Mode == FrameMode.PostTx)
            {
                recorder.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
                return recorder;
            }
        }

        return null;
    }

    /// <summary>Checks a frame transaction's nonce against the sender's state, under either nonce shape.</summary>
    /// <remarks>With <see cref="Transaction.NonceKeys"/> every key must sit at <see cref="Transaction.Nonce"/>,
    /// so the set is consumed as a unit. Assumes the caller has already checked well-formedness.</remarks>
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
        // eth_call and the other estimation/tracing entry points reach the processor with validation
        // skipped, so the whole structural constraint set is enforced here and not only in TxValidator.
        if (!FrameTxValidation.IsWellFormed(tx, spec.IsEip7906Enabled, out string? malformed))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail(malformed!);
        }

        if (tx.NonceKeys is { } nonceKeys)
        {
            if (!spec.IsEip8250Enabled)
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("keyed nonces are not enabled");
            }

            // Structural, so it holds even where validation is skipped: the fixed-size buffers keyed on the set
            // take a well-formed one as their precondition, and eth_call and the simulator arrive without a validator.
            if (!KeyedNonceManager.AreNonceKeysWellFormed(nonceKeys))
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction nonce key set is not well-formed");
            }
        }

        // Every transaction-wide snapshot below is taken on the far side of the frame loop's per-frame
        // discard, so an entry journal left dirty by a caller would make restoring to one throw.
        WorldState.ResetTransient();

        if (opts.HasFlag(ExecutionOptions.FrameValidationPrefixOnly))
        {
            return SimulateFrameValidationPrefix(tx, tracer, opts, header, spec);
        }

        Address sender = tx.SenderAddress!;
        Snapshot txSnapshot = WorldState.TakeSnapshot();

        // Follows SkipValidation as the account-nonce path does: eth_call overwrites the supplied nonce.
        if (ShouldValidate(opts))
        {
            TransactionResult nonceResult = ValidateFrameTxNonce(tx, sender);
            if (!nonceResult)
            {
                WorldState.Restore(txSnapshot);
                return nonceResult;
            }
        }

        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        // EIP-7928: a tx that never takes the P256 branch never accesses the precompile, so no BAL entry.
        IPrecompile? p256Precompile = _codeInfoRepository.GetPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress, spec);
        if (!FrameTxSignatureValidator.Validate(tx, in sigHash, Ecdsa, p256Precompile, spec, out string? signatureError))
        {
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail(signatureError!);
        }

        TxFrame[] frames = tx.Frames!;
        UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out _);
        UInt256 premiumPerGas = UInt256.Zero;
        if (ShouldValidateGas(tx, opts) && !TryCalculatePremiumPerGas(tx, header.BaseFeePerGas, out premiumPerGas))
        {
            TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee.WithDetail(
                $"max fee per gas less than block base fee: address {tx.SenderAddress?.ToString(withEip55Checksum: true) ?? "unknown"}, maxFeePerGas: {tx.MaxFeePerGas}, baseFee: {header.BaseFeePerGas}");
        }

        if (tx.RecentRootReferences is not null && !spec.IsEip8272Enabled)
        {
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail(FrameTxValidation.RecentRootReferencesNotEnabled);
        }

        if (tx.NonceKeys is not null)
        {
            tx.FrameCalldataStats = FrameTxNonceCalldata.Measure(tx);
        }

        // The structural check bounds the frame gas sum alone; the budget it feeds can still overflow.
        tx.ReferenceCalldataStats = RecentRootReferenceDecoder.Instance.Measure(tx.RecentRootReferences);
        if (!FrameTxValidation.TryCalculateGasBudget(tx, spec, out ulong intrinsicGas, out ulong floorGas, out ulong txGasLimit))
        {
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction gas limit overflows");
        }

        // max_cost (TXPARAM 0x06) reserves at max_fee_per_gas, so it bounds the settlement products below;
        // its blob leg is priced at the actual blob_base_fee so the escrow the payer sees is correct.
        UInt256 blobFee = UInt256.Zero;
        if (tx.BlobVersionedHashes is { Length: > 0 })
        {
            if (!_blobBaseFeeCalculator.TryCalculateBlobFees(header, tx, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas, out blobFee))
            {
                TraceLogInvalidTx(tx, "BLOB_BASE_FEE_OVERFLOW");
                WorldState.Restore(txSnapshot);
                return RequiredBalanceExceeds256Bits(tx);
            }

            // EIP-4844: max_fee_per_blob_gas must cover the current blob base fee, else the tx is invalid.
            if (tx.MaxFeePerBlobGas.GetValueOrDefault() < feePerBlobGas)
            {
                TraceLogInvalidTx(tx, "INSUFFICIENT_MAX_FEE_PER_BLOB_GAS");
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.InsufficientSenderBalance.WithDetail(
                    BlockErrorMessages.InsufficientMaxFeePerBlobGas(tx.SenderAddress, tx.MaxFeePerBlobGas, feePerBlobGas));
            }
        }

        if (UInt256.MultiplyOverflow((UInt256)txGasLimit, tx.DecodedMaxFeePerGas, out UInt256 maxCost)
            || UInt256.AddOverflow(maxCost, blobFee, out maxCost))
        {
            TraceLogInvalidTx(tx, "INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE");
            WorldState.Restore(txSnapshot);
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
        // EIP-3529 storage refunds accumulate into a single transaction-scoped counter.
        long refundCounter = 0;

        // EIP-2929 warm/cold journal shared across frames (EIP-8141 § Cross-frame interactions): targets
        // per frame, sender and coinbase once per transaction. ENTRY_POINT-as-caller is unspecified: left cold.
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
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("recent root reference is not committed or out of range");
        }

        // A batch is the maximal run [i, j] where i..j-1 carry ATOMIC_BATCH_FLAG and j does not; any
        // failure inside it rolls back to before the run and skips the rest of it.
        bool inBatch = false;
        StackAccessTracker batchTracker = default;
        FrameCheckpoint batchStart = new(
            Snapshot.Empty, Index: 0, Refund: 0, StateGas: 0, Journal: 0,
            Destroys: accessTracker.DestroyList.TakeSnapshot());

        FrameCheckpoint prefixEnd = new(
            txSnapshot, Index: -1, Refund: 0, StateGas: 0, Journal: 0,
            Destroys: accessTracker.DestroyList.TakeSnapshot());
        bool postTxReverted = false;
        // EIP-161: once any frame touches RIPEMD-160, the touch outlives every later rollback that
        // leaves the transaction valid, so it is tracked for the whole transaction rather than per frame.
        bool shouldRestoreRipemdTouch = false;
        IFrameTxReceiptTracer? frameReceiptTracer = tracer as IFrameTxReceiptTracer;

        for (int i = 0; i < frames.Length; i++)
        {
            TxFrame frame = frames[i];
            frameContext.CurrentFrameIndex = i;

            // A batch begins at the first flagged frame; snapshot the state and log count before it.
            if (!inBatch && frame.IsAtomicBatch)
            {
                inBatch = true;
                batchStart = new FrameCheckpoint(
                    WorldState.TakeSnapshot(), Index: i, Refund: refundCounter, StateGas: totalFrameStateGasUsed,
                    Journal: frameContext.FrameJournalCheckpoint, Destroys: accessTracker.DestroyList.TakeSnapshot());
                batchTracker = accessTracker;
                batchTracker.TakeSnapshot();
            }

            bool isSender = frame.Mode == FrameMode.Sender;
            if (isSender && !frameContext.SenderApproved)
            {
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("SENDER frame before execution approval");
            }

            Address resolvedTarget = frame.Target ?? sender;
            Address caller = isSender ? sender : Eip8141Constants.EntryPointAddress;
            bool isStatic = frame.Mode is FrameMode.Verify or FrameMode.PostTx;

            // ORIGIN returns the frame's caller throughout all call depths.
            VirtualMachine.SetTxExecutionContext(new TxExecutionContext(
                caller, _codeInfoRepository, tx.BlobVersionedHashes, in effectiveGasPrice, frameContext)
            {
                SuppressLogs = ShouldSuppressLogs(opts, tracer),
                MaterializeLogMemory = tracer.IsTracingInstructions || tracer.IsTracingMemory
            });

            // The shared journal accumulates logs across frames; this frame's own logs start here.
            int frameLogStart = accessTracker.Logs.Count;
            int frameStartJournal = frameContext.FrameJournalCheckpoint;
            bool payerWasSet = frameContext.Payer is not null;
            TransactionSubstate substate = ExecuteFrame(frame, resolvedTarget, caller, isStatic, frameContext, in accessTracker, spec, tracer, out ulong frameGasUsed, out long frameStateGas);
            // Transient storage is discarded between frames (EIP-8141 § Cross-frame interactions). Discarded
            // here rather than before the next frame: the batch and prefix-end snapshots straddle frames, and
            // a discard after either was taken truncates the journal it indexes into.
            WorldState.ResetTransient();
            shouldRestoreRipemdTouch |= substate.ShouldRestoreRipemdTouch;

            bool frameSucceeded = !substate.ShouldRevert && !substate.IsError;
            frameReceiptTracer?.ReportFrameEnd(i, frameSucceeded ? null : substate.EvmExceptionType);

            totalFrameGasUsed += frameGasUsed;
            if (frameSucceeded)
            {
                totalFrameStateGasUsed += frameStateGas;
                frameContext.MarkFrameSucceeded(i);
                // A reverted frame's refunds go with its state; an in-batch contribution is unwound below.
                refundCounter += substate.Refund;
            }

            int frameLogCount = accessTracker.Logs.Count - frameLogStart;
            LogEntry[] frameLogs = frameSucceeded && frameLogCount > 0
                ? accessTracker.Logs.AsSpan().Slice(frameLogStart, frameLogCount).ToArray()
                : [];
            ulong frameStateGasUsed = frameSucceeded ? (ulong)frameStateGas : 0;
            frameReceipts[i] = new TxFrameReceipt(
                frameSucceeded ? TxFrameReceipt.StatusSuccess : TxFrameReceipt.StatusFailure,
                frameGasUsed - frameStateGasUsed,
                frameStateGasUsed,
                frameLogs);
            frameContext.RecordFrameReceipt(i, frameGasUsed - frameStateGasUsed, frameStateGasUsed);

            if (frame.Mode == FrameMode.Verify && !frameSucceeded)
            {
                // A failed VERIFY frame invalidates the whole transaction.
                WorldState.Restore(txSnapshot);
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("VERIFY frame reverted");
            }

            if (frame.Mode == FrameMode.PostTx && !frameSucceeded)
            {
                // A failed assertion discards the body down to the validation prefix, overriding any
                // batch unroll, but unlike a VERIFY revert it leaves the transaction valid.
                WorldState.Restore(prefixEnd.Snapshot);
                accessTracker.DestroyList.Restore(prefixEnd.Destroys);
                VirtualMachineStatics.RestoreRipemdTouch(WorldState, spec, shouldRestoreRipemdTouch);
                refundCounter = prefixEnd.Refund;

                // Body logs go with the state that produced them, and the bloom derives from these receipts.
                FrameTxRollback.ScrubReceipts(frameReceipts, frameContext, prefixEnd.Index + 1, i);

                totalFrameGasUsed -= (ulong)(totalFrameStateGasUsed - prefixEnd.StateGas);
                totalFrameStateGasUsed = prefixEnd.StateGas;
                frameContext.RestoreFrameJournal(prefixEnd.Journal);

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
                    // End of the validation prefix: EIP-7906 keeps everything up to here when POST_TX reverts.
                    prefixEnd = new FrameCheckpoint(
                        WorldState.TakeSnapshot(), Index: i, Refund: refundCounter, StateGas: totalFrameStateGasUsed,
                        Journal: frameContext.FrameJournalCheckpoint, Destroys: accessTracker.DestroyList.TakeSnapshot());
                }
            }
            else if (!inBatch)
            {
                frameContext.RestoreFrameJournal(frameStartJournal);
            }

            if (inBatch)
            {
                if (!frameSucceeded)
                {
                    // Unroll: restore pre-batch state and skip the rest (status 0x2); the failed frame
                    // keeps its failure receipt.
                    WorldState.Restore(batchStart.Snapshot);
                    VirtualMachineStatics.RestoreRipemdTouch(WorldState, spec, shouldRestoreRipemdTouch);
                    batchTracker.Restore();

                    // Earlier frames' logs go with their state; status and gas_used stay.
                    FrameTxRollback.ScrubReceipts(frameReceipts, frameContext, batchStart.Index, i);

                    // The unrolled frames' writes are gone with the snapshot, so their state charges
                    // are not owed either; the counter only grows, so the batch-start value undoes them.
                    totalFrameGasUsed -= (ulong)(totalFrameStateGasUsed - batchStart.StateGas);
                    totalFrameStateGasUsed = batchStart.StateGas;
                    frameContext.RestoreFrameJournal(batchStart.Journal);
                    // Refunds from the reverted batch are discarded with its state, so roll the counter back.
                    refundCounter = batchStart.Refund;

                    if (prefixEnd.Index >= batchStart.Index)
                    {
                        // Its snapshot points past the truncated journal; unwinding to it would throw.
                        prefixEnd = batchStart with { Index = batchStart.Index - 1 };
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

        return SettleFrameTx(
            tx: tx,
            tracer: tracer,
            frameReceiptTracer: frameReceiptTracer,
            opts: opts,
            header: header,
            spec: spec,
            frameContext: frameContext,
            frameReceipts: frameReceipts,
            accessTracker: in accessTracker,
            txSnapshot: in txSnapshot,
            intrinsicGas: intrinsicGas,
            floorGas: floorGas,
            totalFrameGasUsed: totalFrameGasUsed,
            totalFrameStateGasUsed: totalFrameStateGasUsed,
            refundCounter: refundCounter,
            effectiveGasPrice: in effectiveGasPrice,
            premiumPerGas: in premiumPerGas,
            blobFee: in blobFee,
            maxCost: in maxCost,
            postTxReverted: postTxReverted);
    }

    /// <summary>Settles a frame transaction once every frame has run: nets the EIP-3529 refund, computes the
    /// payer and block gas, returns the unspent <c>max_cost</c> escrow, credits the fee recipients, finalizes
    /// EIP-6780 destructions and commits or restores the transaction-wide snapshot.</summary>
    /// <remarks>Straight-line tail of <see cref="ExecuteFrameTx"/>, taking the loop's accumulated totals as
    /// inputs. The only failure it can report is a transaction that never set a payer, which unwinds to
    /// <paramref name="txSnapshot"/>.</remarks>
    /// <param name="intrinsicGas">The transaction's intrinsic gas, gross of any frame execution.</param>
    /// <param name="floorGas">The EIP-7623 calldata floor the net charge cannot fall below.</param>
    /// <param name="totalFrameGasUsed">Gas charged across all frames whose effects survived the loop.</param>
    /// <param name="totalFrameStateGasUsed">The state-gas dimension of that total, before correction.</param>
    /// <param name="refundCounter">The EIP-3529 refund accumulated by committed frames.</param>
    /// <param name="maxCost">The escrow charged to the payer at approval, per TXPARAM 0x06.</param>
    /// <param name="postTxReverted">Whether a POST_TX frame discarded the body, which the receipt reports as a failure.</param>
    private TransactionResult SettleFrameTx(
        Transaction tx,
        ITxTracer tracer,
        IFrameTxReceiptTracer? frameReceiptTracer,
        ExecutionOptions opts,
        BlockHeader header,
        IReleaseSpec spec,
        FrameTxContext frameContext,
        TxFrameReceipt[] frameReceipts,
        in StackAccessTracker accessTracker,
        in Snapshot txSnapshot,
        ulong intrinsicGas,
        ulong floorGas,
        ulong totalFrameGasUsed,
        long totalFrameStateGasUsed,
        long refundCounter,
        in UInt256 effectiveGasPrice,
        in UInt256 premiumPerGas,
        in UInt256 blobFee,
        in UInt256 maxCost,
        bool postTxReverted)
    {
        if (frameContext.Payer is null)
        {
            WorldState.Restore(txSnapshot);
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction never set a payer");
        }

        // EIP-3529 refunds are netted once at the transaction level, capped at a fifth of the gross gas;
        // per-frame receipts stay gross of them, and the EIP-7623 floor bounds the net charge from below.
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
        Debug.Assert(refundCounter >= 0, $"frame-tx settlement invariant violated: negative refund counter ({refundCounter}).");
        ulong gasAfterRefund = grossGas - RefundHelper.CalculateClaimableRefund(grossGas, (ulong)Math.Max(0, refundCounter), spec);
        ulong blockStateGas = (ulong)Math.Max(0, totalFrameStateGasUsed - stateGasCorrection);
        // EIP-7778: the payer pays the post-refund execution dimension, but the block counts it before the refund.
        ulong payerRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockExecutionGas(gasAfterRefund, blockStateGas, floorGas);
        ulong blockRegularGas = Eip8037BlockGasInclusionCheck.CalculateBlockExecutionGas(grossGas, blockStateGas, floorGas);
        ulong spentGas = payerRegularGas + blockStateGas;
        // Set explicitly like the regular path: the BlockGasUsed getter otherwise falls back to tx.GasLimit,
        // which for a frame tx is the frame-gas sum rather than the gas spent that block validation sums.
        if (!opts.HasFlag(ExecutionOptions.Warmup)) // only the main thread updates the transaction
        {
            tx.BlockGasUsed = blockRegularGas;
        }
        Address payer = frameContext.Payer;

        // The payer was charged max_cost at approval; refund the remainder, keeping the burned base-fee
        // and blob legs. Both legs are bounded by max_cost, so the subtraction cannot underflow.
        UInt256 spentCost = (UInt256)spentGas * effectiveGasPrice;
        UInt256 chargedCost = spentCost + blobFee;
        if (maxCost > chargedCost)
        {
            WorldState.AddToBalance(payer, maxCost - chargedCost, spec);
        }

        // Fee-collector chains collect the otherwise-burned legs, exactly as PayFees does.
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

        // EIP-7928: fee accounting touches the beneficiary regardless of premium, so the credit is
        // unconditional as in PayFees.
        UInt256 fees = premiumPerGas * (UInt256)spentGas;
        WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec);

        // EIP-6780: finalize committed frames' self-destructs.
        if (accessTracker.DestroyList.Count > 0)
        {
            bool commitDestroys = opts.HasFlag(ExecutionOptions.Commit) && !opts.HasFlag(ExecutionOptions.Restore);
            bool removeSelfdestructBurn = spec.IsEip8246Enabled;
            Debug.Assert(removeSelfdestructBurn && spec.GasCosts.DestroyRefund == 0,
                "frame-tx self-destruct finalization assumes EIP-8246 (balance kept, no burn log) and a zero post-EIP-3529 destroy refund, so it emits no burn log, adds no refund and needs no canonical ordering");
            foreach (Address toBeDestroyed in accessTracker.DestroyList)
            {
                UInt256 destroyedBalance = removeSelfdestructBurn ? WorldState.GetBalance(toBeDestroyed) : default;
                DestroyAccount(WorldState, toBeDestroyed, in destroyedBalance, commitDestroys, removeSelfdestructBurn);
            }
        }

        // CommitAndRestore asks for both, but a commit clears the journals the snapshot indexes into; the
        // frame path journals the whole transaction, so the restore alone suffices.
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
            // Capped at the effective price paid, as in PayFees, so validation-off runs do not over-report.
            tracer.ReportFees(fees, effectiveBaseFee * spentGas + blobFee);
        }

        if (tracer.IsTracingReceipt)
        {
            frameReceiptTracer?.ReportFrameTxReceipt(payer, frameReceipts);

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

    /// <summary>Simulates a frame transaction's validation prefix against read-only head state for mempool
    /// admission, resolving the payer under the <c>MAX_VERIFY_GAS</c> bound.</summary>
    /// <remarks>Nonce equality is deliberately not required: the prefix never reads the account nonce.</remarks>
    private TransactionResult SimulateFrameValidationPrefix(Transaction tx, ITxTracer tracer, ExecutionOptions opts, BlockHeader header, IReleaseSpec spec)
    {
        Address sender = tx.SenderAddress!;
        Snapshot txSnapshot = WorldState.TakeSnapshot();
        try
        {
            using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);
            TransactionResult prepared = PrepareValidationPrefixSimulation(
                tx, opts, header, spec, in accessTracker,
                out FrameTxContext frameContext, out UInt256 effectiveGasPrice, out ulong verifyGasUsed);
            if (!prepared)
            {
                return prepared;
            }

            TxFrame[] frames = tx.Frames!;
            IFrameTxPrefixTracer? prefixTracer = tracer as IFrameTxPrefixTracer;
            for (int i = 0; i < frames.Length; i++)
            {
                TxFrame frame = frames[i];
                bool isDeployFrame = OpensDeployPrefix(frames, i);

                // EIP-8141 § Validation Prefix: the shortest prefix that sets a payer, so a non-VERIFY frame
                // ends it. An opening deploy frame is the sole non-VERIFY frame the prefix admits.
                if (!isDeployFrame && frame.Mode != FrameMode.Verify)
                {
                    break;
                }

                // EIP-8141 forbids the atomic-batch flag on any validation-prefix frame.
                if (frame.IsAtomicBatch)
                {
                    return TransactionResult.ErrorType.MalformedTransaction.WithDetail("atomic batch flag in validation prefix");
                }

                frameContext.CurrentFrameIndex = i;

                TxFrame boundedFrame = CapFrameGas(frame, Eip8141Constants.MaxVerifyGas - verifyGasUsed, out bool capped);

                Address resolvedTarget = frame.Target ?? sender;
                Address caller = Eip8141Constants.EntryPointAddress;

                VirtualMachine.SetTxExecutionContext(new TxExecutionContext(
                    caller, _codeInfoRepository, tx.BlobVersionedHashes, in effectiveGasPrice, frameContext)
                {
                    SuppressLogs = ShouldSuppressLogs(opts, tracer),
                    MaterializeLogMemory = tracer.IsTracingInstructions || tracer.IsTracingMemory
                });

                // The deploy-frame carve-outs are scoped to one frame and everything it calls, which the
                // tracer cannot see from opcodes alone.
                prefixTracer?.StartPrefixFrame(isDeployFrame, resolvedTarget);

                // A deploy frame runs in DEFAULT mode, so unlike a VERIFY frame it may write state.
                TransactionSubstate substate = ExecuteFrame(boundedFrame, resolvedTarget, caller, isStatic: !isDeployFrame, frameContext, in accessTracker, spec, tracer, out ulong frameGasUsed, out long frameStateGas);
                // Discarded once the frame has run, as the main loop does.
                WorldState.ResetTransient();

                verifyGasUsed += frameGasUsed - (ulong)frameStateGas;

                if (substate.ShouldRevert || substate.IsError)
                {
                    // Only a capped frame that ran out of gas proves the prefix exceeds MAX_VERIFY_GAS;
                    // an explicit revert is a within-budget rejection.
                    return capped && !substate.ShouldRevert
                        ? TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction validation prefix exceeds MAX_VERIFY_GAS")
                        : TransactionResult.ErrorType.MalformedTransaction.WithDetail("validation prefix frame reverted");
                }

                frameContext.MarkFrameSucceeded(i);
                // As full execution records it, so a later prefix frame reading gas_used through FRAMEPARAM sees
                // the same value — except behind a capped frame, whose run had the smaller allowance.
                frameContext.RecordFrameReceipt(i, frameGasUsed - (ulong)frameStateGas, (ulong)frameStateGas);

                // A deploy frame that leaves tx.sender codeless would have the VERIFY frames behind it
                // validate against default code instead of the account being deployed.
                if (isDeployFrame && WorldState.GetCodeHash(sender) == Keccak.OfAnEmptyString)
                {
                    return TransactionResult.ErrorType.MalformedTransaction.WithDetail("deploy frame installed no code at tx.sender");
                }

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

    /// <summary>Validates a prefix simulation's transaction-level preconditions and builds its context.</summary>
    private TransactionResult PrepareValidationPrefixSimulation(
        Transaction tx,
        ExecutionOptions opts,
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
        if (!opts.HasFlag(ExecutionOptions.FrameSignaturesPreValidated))
        {
            // As the main path does, so an unused P256 branch records no account access (EIP-7928).
            IPrecompile? p256Precompile = _codeInfoRepository.GetPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress, spec);
            if (!FrameTxSignatureValidator.Validate(tx, in sigHash, Ecdsa, p256Precompile, spec, out string? signatureError))
            {
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail(signatureError!);
            }
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

        // A bound this simulation rolls back, not an escrow: shared with the admission gate so both judge the
        // same number, and its blob leg prices at max_fee_per_blob_gas since the fee at inclusion is unknown here.
        if (!FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 maxCost))
        {
            return TransactionResult.ErrorType.MalformedTransaction.WithDetail("frame transaction maximum cost cannot be priced");
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

        // RECENTROOTREFLOAD reads the envelope on the strength of this check, so it precedes the prefix.
        // Anchored to the earliest slot the tx could execute in: the head slot is referenceable only from the next.
        ulong? executionSlot = header.SlotNumber is { } headSlot ? headSlot + 1 : null;
        return RecentRootReferences.Validate(WorldState, tx.RecentRootReferences, executionSlot, in accessTracker)
            ? TransactionResult.Ok
            : TransactionResult.ErrorType.MalformedTransaction.WithDetail("recent root reference is not committed or out of range");
    }

    /// <summary>Whether frame <paramref name="i"/> is a <c>deploy</c> frame opening the validation prefix.</summary>
    /// <remarks>Positional, as RecognizedPrefixLength reaches index 1 only past an expiry-verify frame at index 0.
    /// Spells the same prologue rule as <see cref="FrameTxValidation.ApprovalSearchStart"/>; a grammar change touches both.</remarks>
    private static bool OpensDeployPrefix(TxFrame[] frames, int i) =>
        (i == 0 || (i == 1 && FrameTxValidation.IsExpiryVerifyFrame(frames[0])))
        && i + 1 < frames.Length
        && FrameTxValidation.IsDeployFrame(frames[i])
        && frames[i + 1].Mode == FrameMode.Verify;

    private TransactionSubstate ExecuteFrame(TxFrame frame, Address resolvedTarget, Address caller, bool isStatic, FrameTxContext frameContext, in StackAccessTracker accessTracker, IReleaseSpec spec, ITxTracer tracer, out ulong gasUsed, out long stateGasUsed)
    {
        stateGasUsed = 0;
        UInt256 value = frame.Value;

        // create_evm_from_frame: the frame pays its target's access out of its own gas limit before the
        // balance check and before dispatch, resolving the target's code being what dispatch is. The charge
        // is therefore uniform across contract code, delegated code and the default code. Precompiles are
        // checked explicitly because EIP-2929 pre-warms them without the shared tracker holding them.
        ulong entryExecution = spec.UseHotAndColdStorage
            ? (accessTracker.IsCold(resolvedTarget) && !spec.IsPrecompile(resolvedTarget)
                ? TGasPolicy.GetColdAccountAccessCost(spec)
                : Eip8038Constants.WarmAccess)
            : 0;
        // Checked before the balance and deadness queries below, which are themselves recorded reads: a frame
        // that cannot afford its target's access must leave the target untouched, as the CALL path does.
        if (entryExecution > frame.ExecutionGasLimit)
        {
            gasUsed = frame.ExecutionGasLimit;
            return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
        }

        // The charge prices reading the target's account, so the read is recorded even where the frame
        // fails before dispatch would have read the code: EIP-7928 does not unwind a reverted frame's reads.
        WorldState.AddAccountRead(resolvedTarget);

        // As with an ordinary CALL, a caller unable to fund the value transfer reverts the frame,
        // consuming the gas charged so far.
        if (!value.IsZero && WorldState.GetBalance(caller) < value)
        {
            gasUsed = entryExecution;
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        // EIP-8141: a precompile dispatches in every mode, leaving default code to a VERIFY frame's codeless
        // non-precompile target. The repository decides what is a precompile, being what dispatches the frame.
        if (frame.Mode == FrameMode.Verify
            && _codeInfoRepository.GetPrecompile(resolvedTarget, spec) is null
            && WorldState.GetCodeHash(resolvedTarget) == Keccak.OfAnEmptyString)
        {
            TransactionSubstate defaultCode = ExecuteDefaultVerifyCode(frame, resolvedTarget, frameContext, spec, in accessTracker, tracer, entryExecution, out gasUsed, out stateGasUsed);
            // The entry charge warms the target on this path too; a failing default-code frame is a failing
            // VERIFY frame, which invalidates the transaction, so there is nothing to unwind.
            if (spec.UseHotAndColdStorage && !defaultCode.IsError && !defaultCode.ShouldRevert)
            {
                accessTracker.WarmUp(resolvedTarget);
            }

            return defaultCode;
        }

        // No pre-EIP-8037 form: EIP-8141 is only composed onto specs carrying it.
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

            // Read only once the access is paid for. EIP-7702: a precompile must not execute via delegation,
            // asked of the repository because that is what dispatches: a state override can move a precompile.
            WorldState.AddAccountRead(delegation);
            codeInfo = _codeInfoRepository.GetPrecompile(delegation, spec) is not null
                ? CodeInfo.Empty
                : _codeInfoRepository.GetCachedCodeInfoNoDelegation(delegation, spec);
        }

        ReadOnlyMemory<byte> inputData = frame.Data;

        // VmState.Dispose releases its environment only below the top level, so this one is caller-owned;
        // declared before the VmState so it returns to the pool after it.
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

        // EIP-8141: a reverting frame also reverts its warm/cold touches, so snapshot before warming.
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

        // Selected explicitly: the parameterless ExecuteTransaction overload hard-codes OffFlag.
        // DispatchFlags folds the tracing arm out of the zkEVM guest, which compiles no tracing dispatch.
        TransactionSubstate substate = !DispatchFlags.Tracing(tracer.IsTracingInstructions)
            ? VirtualMachine.ExecuteTransaction(state, WorldState, tracer)
            : VirtualMachine.ExecuteTransaction<OnFlag>(state, WorldState, tracer);

        long stateReservoirSeed = frame.StateGasLimit > long.MaxValue ? long.MaxValue : (long)frame.StateGasLimit;
        if (substate.IsError || substate.ShouldRevert)
        {
            TGasPolicy.ResetForHalt(ref state.Gas, stateReservoirSeed, 0);
        }

        ulong combinedLimit = frame.ExecutionGasLimit.SaturatingAdd(frame.StateGasLimit);
        gasUsed = substate.IsError
            ? combinedLimit - (ulong)Math.Max(0, TGasPolicy.GetStateReservoir(in state.Gas))
            : TGasPolicy.GetPreRefundGas(in state.Gas, combinedLimit);
        stateGasUsed = Math.Max(0, TGasPolicy.GetStateGasUsed(in state.Gas));

        if (substate.ShouldRevert || substate.IsError)
        {
            WorldState.Restore(snapshot);
            // The machine re-applies the EIP-161 RIPEMD touch over its own rollback; this restore
            // rewinds past that, so it has to be re-applied here too.
            VirtualMachineStatics.RestoreRipemdTouch(WorldState, spec, substate.ShouldRestoreRipemdTouch);
            frameTracker.Restore();
        }

        return substate;
    }

    /// <summary>
    /// EIP-8141 default code of a <c>VERIFY</c> frame whose target has no code: require a canonical-hash
    /// SECP256K1 signature signed by the target, then APPROVE. The default code draws no execution gas of
    /// its own beyond the frame's entry access charge, which is already accounted for.
    /// The signature's cryptographic validity is already checked in pre-flight; default code checks
    /// only the structural conditions the spec pins.
    /// </summary>
    /// <remarks>The approval runs the same guards and effects as the <c>APPROVE</c> opcode, so a default-code
    /// frame the approval context refuses fails exactly as an opcode approval would.</remarks>
    /// <param name="entryExecution">
    /// Execution gas the frame already owes for its target's access, charged before dispatch and known to
    /// fit within <see cref="TxFrame.ExecutionGasLimit"/>.
    /// </param>
    private TransactionSubstate ExecuteDefaultVerifyCode(TxFrame frame, Address resolvedTarget, FrameTxContext frameContext, IReleaseSpec spec, in StackAccessTracker accessTracker, ITxTracer tracer, ulong entryExecution, out ulong gasUsed, out long stateGasUsed)
    {
        gasUsed = entryExecution;
        stateGasUsed = 0;

        FrameFlags allowedScope = frame.AllowedApproveScope;
        if (allowedScope == 0)
        {
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        int sigIndex = (allowedScope & FrameFlags.ApproveExecution) != 0 ? 0 : 1;
        TxFrameSignature[] signatures = frameContext.Signatures;
        if (signatures.Length <= sigIndex
            || signatures[sigIndex].Scheme != TxFrameSignature.SchemeSecp256k1
            || !signatures[sigIndex].Msg.IsEmpty
            || frameContext.ResolvedSigner(sigIndex) != resolvedTarget)
        {
            return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
        }

        FrameApprovalOutcome outcome = frameContext.PlanApproval(allowedScope, resolvedTarget, WorldState, out FrameApprovalPlan plan);
        if (outcome != FrameApprovalOutcome.Approved)
        {
            if (outcome == FrameApprovalOutcome.Rejected)
            {
                return new TransactionSubstate(EvmExceptionType.Revert, tracer.IsTracingInstructions);
            }

            gasUsed = frame.ExecutionGasLimit;
            return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
        }

        // The declared limit still is state_gas_left: default code writes no state before the approval, so
        // it never draws on the state dimension. A state charge added ahead of this would silently undercharge.
        long nonceStateGas = frameContext.NonceStateGas<TGasPolicy>(in plan, WorldState);
        if ((ulong)nonceStateGas > frame.StateGasLimit)
        {
            gasUsed = frame.ExecutionGasLimit;
            return new TransactionSubstate(EvmExceptionType.OutOfGas, tracer.IsTracingInstructions);
        }

        stateGasUsed = nonceStateGas;
        gasUsed += (ulong)nonceStateGas;

        frameContext.ApplyApproval(in plan, resolvedTarget, WorldState, spec, in accessTracker);
        return DefaultCodeSuccess();
    }

    private static TransactionSubstate DefaultCodeSuccess() =>
        new(ReadOnlyMemory<byte>.Empty, refund: 0, destroyList: null, logs: null, shouldRevert: false);
}

/// <summary>A point in the frame loop a later failure can unwind the transaction to.</summary>
/// <remarks>The six members are only meaningful together: restoring <see cref="Snapshot"/> without
/// <see cref="Journal"/> would leave the frame journal indexing past its truncated end, and unwinding to it
/// would throw. Holding them as one value is what stops the two checkpoints the loop keeps from drifting.
/// The batch's <see cref="StackAccessTracker"/> copy stays outside: it carries its own snapshot/restore pair,
/// and the prefix-end checkpoint has no counterpart to it.</remarks>
/// <param name="Snapshot">World-state snapshot taken at the checkpoint.</param>
/// <param name="Index">Index of the last frame whose effects the checkpoint includes.</param>
/// <param name="Refund">The EIP-3529 refund counter at the checkpoint.</param>
/// <param name="StateGas">Accumulated frame state gas at the checkpoint.</param>
/// <param name="Journal">The frame journal position at the checkpoint.</param>
/// <param name="Destroys">The EIP-6780 destroy-list position at the checkpoint.</param>
file readonly record struct FrameCheckpoint(
    Snapshot Snapshot,
    int Index,
    long Refund,
    long StateGas,
    int Journal,
    int Destroys);

/// <summary>Frame-loop rollback bookkeeping that does not depend on the processor's gas policy.</summary>
file static class FrameTxRollback
{
    /// <summary>Drops the logs and state-gas charge from the receipts of frames over the half-open range
    /// <paramref name="from"/>..<paramref name="to"/>, whose writes a rollback has just discarded.</summary>
    /// <remarks>Status and execution gas stay: those frames ran and are charged for it. Callers pass the
    /// first rolled-back frame and the frame that failed, which keeps its own receipt.</remarks>
    public static void ScrubReceipts(TxFrameReceipt[] frameReceipts, FrameTxContext frameContext, int from, int to)
    {
        for (int s = from; s < to; s++)
        {
            TxFrameReceipt receipt = frameReceipts[s];
            if (receipt.Logs.Length > 0 || receipt.StateGasUsed > 0)
            {
                frameReceipts[s] = new TxFrameReceipt(receipt.Status, receipt.ExecutionGasUsed, 0, []);
                frameContext.ClearFrameStateGasUsed(s);
            }
        }
    }
}
