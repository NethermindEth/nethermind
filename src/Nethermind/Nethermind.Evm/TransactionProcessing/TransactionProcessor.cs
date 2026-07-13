// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Validation;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public sealed class TransactionProcessor<TGasPolicy>(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine<TGasPolicy>? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager,
        bool parallel = false)
        : TransactionProcessorBase<TGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
    }

    /// <summary>
    /// Non-generic TransactionProcessor for backward compatibility with EthereumGasPolicy.
    /// </summary>
    public sealed class EthereumTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager,
        bool parallel = false)
        : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);

    public class BlobBaseFeeCalculator : ITransactionProcessor.IBlobBaseFeeCalculator
    {
        public static BlobBaseFeeCalculator Instance { get; } = new BlobBaseFeeCalculator();

        public bool TryCalculateBlobFees(BlockHeader header, Transaction transaction,
            ulong blobGasPriceUpdateFraction, out UInt256 feePerBlobGas, out UInt256 totalBlobBaseFee)
        {
            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(header, blobGasPriceUpdateFraction, out feePerBlobGas))
            {
                totalBlobBaseFee = UInt256.Zero;
                return false;
            }
            return BlobGasCalculator.TryCalculateBlobBaseFee(header, transaction, blobGasPriceUpdateFraction, out totalBlobBaseFee);
        }
    }

    public abstract class TransactionProcessorBase
    {
        internal static bool ForceSimpleTransferDisabled;

        private protected static void DestroyAccount(IWorldState worldState, Address toBeDestroyed, in UInt256 balance, bool commit, bool removeSelfdestructBurn)
        {
            // Build-up rounds (!commit) span the whole block: later txs may redeploy this address,
            // so the order-preserving journaled clear is required; the O(1) mark needs a commit after.
            if (commit) worldState.MarkStorageDestroyed(toBeDestroyed);
            else worldState.ClearStorage(toBeDestroyed);
            worldState.DeleteAccount(toBeDestroyed);

            // EIP-8246: preserve any remaining balance as a fresh nonce-0, code-less account;
            // an empty account stays deleted via EIP-161.
            if (removeSelfdestructBurn && !balance.IsZero)
            {
                worldState.CreateAccount(toBeDestroyed, balance);
            }
        }
    }

    public abstract class TransactionProcessorBase<TGasPolicy> : TransactionProcessorBase, ITransactionProcessor
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        protected EthereumEcdsa Ecdsa { get; }
        protected ILogger Logger { get; }
        protected ISpecProvider SpecProvider { get; }
        protected IWorldState WorldState { get; }
        protected IVirtualMachine<TGasPolicy> VirtualMachine { get; }
        protected readonly ICodeInfoRepository _codeInfoRepository;
        private readonly bool _isCodeOverridable;
        private SystemTransactionProcessor<TGasPolicy>? _systemTransactionProcessor;
        protected readonly ITransactionProcessor.IBlobBaseFeeCalculator _blobBaseFeeCalculator;
        protected readonly ILogManager _logManager;
        private readonly bool _parallel;
        private ulong _blockCumulativeRegularGas;
        private ulong _blockCumulativeStateGas;

        protected TransactionProcessorBase(
            ITransactionProcessor.IBlobBaseFeeCalculator? blobBaseFeeCalculator,
            ISpecProvider? specProvider,
            IWorldState? worldState,
            IVirtualMachine<TGasPolicy>? virtualMachine,
            ICodeInfoRepository? codeInfoRepository,
            ILogManager? logManager,
            bool parallel = false)
        {
            ArgumentNullException.ThrowIfNull(logManager);
            ArgumentNullException.ThrowIfNull(specProvider);
            ArgumentNullException.ThrowIfNull(worldState);
            ArgumentNullException.ThrowIfNull(virtualMachine);
            ArgumentNullException.ThrowIfNull(codeInfoRepository);
            ArgumentNullException.ThrowIfNull(blobBaseFeeCalculator);

            Logger = logManager.GetClassLogger(typeof(TransactionProcessorBase<>));
            SpecProvider = specProvider;
            WorldState = worldState;
            VirtualMachine = virtualMachine;
            _codeInfoRepository = codeInfoRepository;
            _isCodeOverridable = codeInfoRepository.IsCodeOverridable;
            _blobBaseFeeCalculator = blobBaseFeeCalculator;

            Ecdsa = new EthereumEcdsa(specProvider.ChainId);
            _logManager = logManager;
            _parallel = parallel;
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
            _blockCumulativeRegularGas = 0;
            _blockCumulativeStateGas = 0;
            VirtualMachine.SetBlockExecutionContext(in blockExecutionContext);
        }

        public void SetBlockExecutionContext(BlockHeader header)
        {
            IReleaseSpec spec = SpecProvider.GetSpec(header);
            BlockExecutionContext blockExecutionContext = new(header, spec);
            SetBlockExecutionContext(in blockExecutionContext);
        }

        public TransactionResult Process(
            Transaction transaction,
            ITxTracer txTracer,
            ExecutionOptions options)
        {
            if (options == ExecutionOptions.BuildUp)
            {
                WorldState.TakeSnapshot(true);
            }

            return ExecuteCore(transaction, txTracer, options);
        }
        private SystemTransactionProcessor<TGasPolicy> GetOrCreateSystemTransactionProcessor()
        {
            if (_systemTransactionProcessor is null)
            {
                Interlocked.CompareExchange(ref _systemTransactionProcessor, CreateSystemTransactionProcessor(), null);
            }
            return _systemTransactionProcessor;
        }

        /// <summary>
        /// Builds the per-instance system-transaction processor. Override to substitute an
        /// engine-specific variant (e.g. AuRa returns <c>AuRaSystemTransactionProcessor</c>).
        /// </summary>
        protected virtual SystemTransactionProcessor<TGasPolicy> CreateSystemTransactionProcessor() =>
            new(_blobBaseFeeCalculator, SpecProvider, WorldState, VirtualMachine, _codeInfoRepository, _logManager);

        private TransactionResult ExecuteCore(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
        {
            if (Logger.IsTrace) Logger.Trace($"Executing tx {tx.Hash}");
            // Warmup keeps real fee/nonce semantics, so it must not route to the system processor.
            if (tx.IsSystem() || opts == ExecutionOptions.SkipValidation)
            {
                return GetOrCreateSystemTransactionProcessor().Execute(tx, tracer, opts);
            }

            TransactionResult result = Execute(tx, tracer, opts);
            if (Logger.IsTrace) Logger.Trace($"Tx {tx.Hash} was executed, {result}");
            return result;
        }

        protected virtual TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
        {
            BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
            IReleaseSpec spec = GetSpec(header);
            IntrinsicGas<TGasPolicy> intrinsicGas = CalculateIntrinsicGas(tx, spec, header.GasLimit);
            return Execute(tx, tracer, opts, header, spec, in intrinsicGas);
        }

        [SkipLocalsInit]
        private TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts, BlockHeader header, IReleaseSpec spec, in IntrinsicGas<TGasPolicy> intrinsicGas)
        {
            // restore is CallAndRestore - previous call, we will restore state after the execution
            bool restore = opts.HasFlag(ExecutionOptions.Restore);
            // commit - is for standard execute, we will commit the state after execution
            // !commit - is for build up during block production, we won't commit state after each transaction to support rollbacks
            // we commit only after all block is constructed
            bool commit = opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) && !spec.IsEip658Enabled);

            TransactionResult result;
            if (!(result = ValidateStatic(tx, header, spec, opts, in intrinsicGas))) return result;

            UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 opcodeGasPrice);

            UpdateMetrics(opts, effectiveGasPrice);

            bool deleteCallerAccount = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);

            if (!(result = ValidateSender(tx, header, spec, tracer, opts)) ||
                !(result = BuyGas(tx, spec, tracer, opts, effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)) ||
                !(result = IncrementNonce(tx, header, spec, tracer, opts)))
            {
                if (restore)
                {
                    WorldState.Reset(resetBlockChanges: false);
                }
                return result;
            }

            Address? recipient = tx.To;
            bool useSimpleTransferFastPath = TryPrepareSimpleTransferFastPath(tx, spec, out CodeInfo? preloadedCodeInfo, out Address? preloadedDelegationAddress);

            bool commitBeforeExecution = commit && (!useSimpleTransferFastPath || restore || tracer.IsTracingState);
            if (commitBeforeExecution) WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullTxTracer.Instance, commitRoots: false);

            if (!(result = CalculateAvailableGas(tx, spec, in intrinsicGas, out TGasPolicy gasAvailable))) return result;

            return useSimpleTransferFastPath
                ? ExecuteSimpleTransfer(tx, header, spec, tracer, opts, restore, commit, deleteCallerAccount, recipient!, in intrinsicGas, gasAvailable, in opcodeGasPrice, in premiumPerGas, in senderReservedGasPayment, in blobBaseFee)
                : ExecuteEvmTransaction(tx, header, spec, tracer, opts, restore, commit, deleteCallerAccount, in intrinsicGas, gasAvailable, in opcodeGasPrice, in premiumPerGas, in senderReservedGasPayment, in blobBaseFee, preloadedCodeInfo, preloadedDelegationAddress);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSimpleTransferFastPathCandidate(Transaction tx, bool isCodeOverridable)
            => !isCodeOverridable && tx.To is not null && tx.AuthorizationList is null && !ForceSimpleTransferDisabled;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasNoExecutableCode(CodeInfo codeInfo, Address? delegationAddress)
            => delegationAddress is null && codeInfo.IsEmpty;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryPrepareSimpleTransferFastPath(
            Transaction tx,
            IReleaseSpec spec,
            out CodeInfo? preloadedCodeInfo,
            out Address? preloadedDelegationAddress)
        {
            preloadedCodeInfo = null;
            preloadedDelegationAddress = null;
            if (!IsSimpleTransferFastPathCandidate(tx, _isCodeOverridable)) return false;

            preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(tx.To!, followDelegation: true, spec, out preloadedDelegationAddress);
            return HasNoExecutableCode(preloadedCodeInfo, preloadedDelegationAddress);
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private TransactionResult ExecuteEvmTransaction(
            Transaction tx,
            BlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            ExecutionOptions opts,
            bool restore,
            bool commit,
            bool deleteCallerAccount,
            in IntrinsicGas<TGasPolicy> intrinsicGas,
            TGasPolicy gasAvailable,
            in UInt256 opcodeGasPrice,
            in UInt256 premiumPerGas,
            in UInt256 senderReservedGasPayment,
            in UInt256 blobBaseFee,
            CodeInfo? preloadedCodeInfo,
            Address? preloadedDelegationAddress)
        {
            VirtualMachine.SetTxExecutionContext(new(tx.SenderAddress!, _codeInfoRepository, tx.BlobVersionedHashes, in opcodeGasPrice));
            // Top-level CREATE tx; the opcode-level CREATE/CREATE2 path bumps this counter from EvmInstructions.Create.
            if (tx.IsContractCreation) Metrics.IncrementCreates();
            // substate.Logs contains a reference to accessTracker.Logs so we can't Dispose until end of the method
            using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);
            long delegationRefunds = 0;
            long delegationAuthBaseRefunds = 0;
            TransactionResult result;
            if (!(result = Validate8037DelegationRefundBounds(tx, spec, in gasAvailable))) return result;

            if (spec.IsEip7702Enabled && tx.HasAuthorizationList)
            {
                delegationRefunds = ProcessDelegations(tx, spec, accessTracker, out delegationAuthBaseRefunds);
            }

            if (!(result = Apply8037DelegationRefunds(tx, spec, in intrinsicGas, ref gasAvailable, ref delegationRefunds, ref delegationAuthBaseRefunds))) return result;

            if (!(result = BuildExecutionEnvironment(tx, spec, _codeInfoRepository, accessTracker, preloadedCodeInfo, preloadedDelegationAddress, out ExecutionEnvironment e))) return result;
            using ExecutionEnvironment env = e;

            // EIP-8037 top-frame charges. At most one applies: a delegated recipient has code and is
            // therefore never a dead account, hence the if/else-if.
            bool recipientIsDelegated = spec.IsEip7702Enabled && tx.To is not null
                && _codeInfoRepository.TryGetDelegation(tx.To, spec, out _);

            // The flag defers the halt to ExecuteEvmCall so the value transfer rolls back and the
            // sender forfeits all gas; merely draining the gas would let a zero-cost frame succeed.
            bool topFrameOutOfGas = false;

            // A new (dead) recipient — including an empty precompile — pays NEW_ACCOUNT state gas.
            if (spec.IsEip8037Enabled && !tx.IsContractCreation && !tx.ValueRef.IsZero
                && tx.To is not null && tx.SenderAddress != tx.To
                && WorldState.IsDeadAccount(tx.To))
            {
                topFrameOutOfGas = !TGasPolicy.ConsumeStateGas(ref gasAvailable, TGasPolicy.GetNewAccountStateCost());
            }
            // EIP-8037: the delegation target costs one flat cold account access (its sole charge —
            // it is only pre-warmed for the frame). Read post-authorization, so same-tx installs count.
            else if (spec.IsEip8037Enabled && !tx.IsContractCreation && recipientIsDelegated)
            {
                ulong delegationCold = spec.IsEip8038Enabled ? Eip8038Constants.ColdAccountAccess : GasCostOf.ColdAccountAccess;
                topFrameOutOfGas = !TGasPolicy.UpdateGas(ref gasAvailable, delegationCold);
            }

            int statusCode = !tracer.IsTracingInstructions ?
                ExecuteEvmCall<OffFlag>(tx, header, spec, tracer, opts, delegationRefunds, intrinsicGas, accessTracker, gasAvailable, env, topFrameOutOfGas, out TransactionSubstate substate, out GasConsumed spentGas) :
                ExecuteEvmCall<OnFlag>(tx, header, spec, tracer, opts, delegationRefunds, intrinsicGas, accessTracker, gasAvailable, env, topFrameOutOfGas, out substate, out spentGas);

            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, blobBaseFee, statusCode);

            // EIP-8037+EIP-7708: process destroy list after PayFees so burn logs include
            // the priority fee in the destroyed account's balance.
            if (spec.IsEip8037Enabled && spec.IsEip7708Enabled && statusCode == StatusCode.Success)
            {
                JournalSet<Address>? destroyList = substate.DestroyList;
                if (destroyList is not null)
                {
                    int count = destroyList.Count;
                    bool removeSelfdestructBurn = spec.IsEip8246Enabled;
                    bool tracingRefunds = tracer.IsTracingRefunds;
                    long destroyRefund = (long)spec.GasCosts.DestroyRefund;
                    if (count > 1)
                    {
                        Address[] buffer = SafeArrayPool<Address>.Shared.Rent(count);
                        destroyList.CopyTo(buffer, 0);
                        buffer.AsSpan(0, count).Sort(default(AddressByBytesComparer));
                        for (int i = 0; i < count; i++)
                        {
                            FinalizeDestroyedAccount(WorldState, in substate, buffer[i], commit, removeSelfdestructBurn);
                            if (tracingRefunds) tracer.ReportRefund(destroyRefund);
                        }
                        SafeArrayPool<Address>.Shared.Return(buffer);
                    }
                    else if (count == 1)
                    {
                        FinalizeDestroyedAccount(WorldState, in substate, destroyList.First, commit, removeSelfdestructBurn);
                        if (tracingRefunds) tracer.ReportRefund(destroyRefund);
                    }
                }

                static void FinalizeDestroyedAccount(IWorldState worldState, in TransactionSubstate substate, Address toBeDestroyed, bool commit, bool removeSelfdestructBurn)
                {
                    UInt256 balance = worldState.GetBalance(toBeDestroyed);
                    // Post-fee path: the burn covers the whole balance incl. priority fees, hence a
                    // Burn (not SelfDestruct) log; EIP-8246 removes the burn and its log entirely.
                    if (!balance.IsZero && !removeSelfdestructBurn)
                    {
                        substate.Logs.Add(TransferLog.CreateBurn(toBeDestroyed, balance));
                    }

                    DestroyAccount(worldState, toBeDestroyed, in balance, commit, removeSelfdestructBurn);
                }
            }

            return FinalizeTransaction(tx, spec, tracer, opts, restore, commit, deleteCallerAccount, in senderReservedGasPayment, env.ExecutingAccount, in substate, spentGas, statusCode);
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private TransactionResult ExecuteSimpleTransfer(
            Transaction tx,
            BlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            ExecutionOptions opts,
            bool restore,
            bool commit,
            bool deleteCallerAccount,
            Address recipient,
            in IntrinsicGas<TGasPolicy> intrinsicGas,
            TGasPolicy gasAvailable,
            in UInt256 opcodeGasPrice,
            in UInt256 premiumPerGas,
            in UInt256 senderReservedGasPayment,
            in UInt256 blobBaseFee)
        {
            Metrics.IncrementEmptyCalls();

            ref readonly UInt256 value = ref tx.ValueRef;
            bool hasValueTransfer = !value.IsZero;
            bool senderIsRecipient = tx.SenderAddress == recipient;
            bool isTracingActions = tracer.IsTracingActions;

            // EIP-8037: a value transfer materialising a new (dead) recipient — including an empty
            // precompile — pays NEW_ACCOUNT state gas; if uncovered, no value moves and all gas is forfeit.
            bool newAccountOutOfGas = false;
            if (spec.IsEip8037Enabled && hasValueTransfer && !senderIsRecipient
                && WorldState.IsDeadAccount(recipient))
            {
                newAccountOutOfGas = !TGasPolicy.ConsumeStateGas(ref gasAvailable, TGasPolicy.GetNewAccountStateCost());
                if (newAccountOutOfGas)
                    TGasPolicy.Consume(ref gasAvailable, TGasPolicy.GetRemainingGas(in gasAvailable));
            }

            // Self-send: sender account is already touched/warmed by gas charging and any
            // +/- value balance ops would cancel to a net no-op, so skip both state writes.
            if (!senderIsRecipient && !newAccountOutOfGas)
            {
                if (hasValueTransfer) PayValue(tx, spec, opts);
                WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec);
            }

            JournalCollection<LogEntry>? logs = null;
            if (spec.IsEip7708Enabled && hasValueTransfer && !senderIsRecipient && !newAccountOutOfGas)
            {
                LogEntry transferLog = TransferLog.CreateTransfer(tx.SenderAddress!, recipient, in value);
                logs = [transferLog];
                if (tracer.IsTracingLogs) tracer.ReportLog(transferLog);
            }

            // Keep tracer event order aligned with VirtualMachine.ExecuteCall.
            if (isTracingActions)
            {
                TraceSimpleTransferActionStart(tx, recipient, tracer, in value, in gasAvailable);
            }

            TransactionSubstate substate = new(
                bytes: default,
                refund: 0,
                destroyList: null,
                logs: logs,
                shouldRevert: false,
                isTracerConnected: false, // safe: the ctor reads this only when shouldRevert is true
                logger: Logger);

            if (isTracingActions)
            {
                tracer.ReportActionEnd(TGasPolicy.GetRemainingGas(in gasAvailable), default);
            }

            TGasPolicy floorGas = intrinsicGas.FloorGas;
            TGasPolicy standardGas = intrinsicGas.Standard;
            long postIntrinsicStateReservoir = TGasPolicy.GetStateReservoir(in gasAvailable);
            GasConsumed spentGas = Refund(tx, header, spec, opts, in substate, in gasAvailable, in opcodeGasPrice, codeInsertRefunds: 0, in floorGas, in standardGas, postIntrinsicStateReservoir);

            int statusCode = newAccountOutOfGas ? StatusCode.Failure : StatusCode.Success;

            if (tracer.IsTracingAccess)
            {
                ReportSimpleTransferAccess(tx, spec, tracer, recipient);
            }

            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, blobBaseFee, statusCode);
            return FinalizeTransaction(tx, spec, tracer, opts, restore, commit, deleteCallerAccount, in senderReservedGasPayment, recipient, in substate, spentGas, statusCode);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TraceSimpleTransferActionStart(Transaction tx, Address recipient, ITxTracer tracer, in UInt256 value, in TGasPolicy gasAvailable)
        {
            tracer.ReportAction(
                TGasPolicy.GetRemainingGas(in gasAvailable),
                value,
                tx.SenderAddress!,
                recipient,
                tx.Data,
                ExecutionType.TRANSACTION);

            if (tracer.IsTracingCode)
            {
                tracer.ReportByteCode(default);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReportSimpleTransferAccess(Transaction tx, IReleaseSpec spec, ITxTracer tracer, Address recipient)
        {
            using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);
            WarmUpTxAccesses(tx, spec, in accessTracker, recipient);
            tracer.ReportAccess(accessTracker.AccessedAddresses, accessTracker.AccessedStorageCells);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WarmUpTxAccesses(Transaction tx, IReleaseSpec spec, in StackAccessTracker accessTracker, Address recipient)
        {
            if (!spec.UseHotAndColdStorage) return;

            if (spec.UseTxAccessLists)
                accessTracker.WarmUp(tx.AccessList); // eip-2930

            if (spec.AddCoinbaseToTxAccessList)
                accessTracker.WarmUp(VirtualMachine.BlockExecutionContext.Header.GasBeneficiary!);

            accessTracker.WarmUp(recipient);
            accessTracker.WarmUp(tx.SenderAddress!);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateHeaderGasUsedAndPayFees(
            Transaction tx,
            BlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            ExecutionOptions opts,
            in TransactionSubstate substate,
            in GasConsumed spentGas,
            in UInt256 premiumPerGas,
            in UInt256 blobBaseFee,
            int statusCode)
        {
            if (!opts.HasFlag(ExecutionOptions.SkipValidation) && !_parallel)
            {
                if (spec.IsEip8037Enabled)
                {
                    _blockCumulativeRegularGas += spentGas.EffectiveBlockGas;
                    _blockCumulativeStateGas += spentGas.BlockStateGas;
                    header.GasUsed = TGasPolicy.CombineBlockGas(_blockCumulativeRegularGas, _blockCumulativeStateGas);
                }
                else
                {
                    header.GasUsed += spentGas.EffectiveBlockGas;
                }
            }

            PayFees(tx, header, spec, tracer, in substate, spentGas.SpentGas, premiumPerGas, blobBaseFee, statusCode);
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private TransactionResult FinalizeTransaction(
            Transaction tx,
            IReleaseSpec spec,
            ITxTracer tracer,
            ExecutionOptions opts,
            bool restore,
            bool commit,
            bool deleteCallerAccount,
            in UInt256 senderReservedGasPayment,
            Address executingAccount,
            in TransactionSubstate substate,
            GasConsumed spentGas,
            int statusCode)
        {
            if (!opts.HasFlag(ExecutionOptions.Warmup))
            {
                tx.BlockGasUsed = spentGas.EffectiveBlockGas;
            }

            //only main thread updates transaction
            if (!opts.HasFlag(ExecutionOptions.Warmup))
                tx.SpentGas = spentGas.SpentGas;

            // Finalize
            if (restore)
            {
                WorldState.Reset(resetBlockChanges: false);
                if (deleteCallerAccount)
                {
                    WorldState.DeleteAccount(tx.SenderAddress!);
                }
                else
                {
                    if (!senderReservedGasPayment.IsZero)
                    {
                        WorldState.AddToBalance(tx.SenderAddress!, senderReservedGasPayment, spec);
                    }

                    DecrementNonce(tx);

                    WorldState.Commit(spec, commitRoots: false);
                }
            }
            else if (commit)
            {
                WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);
            }
            else
            {
                WorldState.ResetTransient();
            }

            if (tracer.IsTracingReceipt)
            {
                Hash256 stateRoot = null;
                if (!spec.IsEip658Enabled)
                {
                    WorldState.RecalculateStateRoot();
                    stateRoot = WorldState.StateRoot;
                }

                if (statusCode == StatusCode.Failure)
                {
                    byte[] output = substate.ShouldRevert ? substate.Output.ToArray() : [];
                    tracer.MarkAsFailed(executingAccount, spentGas, output, substate.Error, stateRoot);
                }
                else
                {
                    LogEntry[] logs = substate.Logs.Count != 0 ? substate.LogsToArray() : [];
                    tracer.MarkAsSuccess(executingAccount, spentGas, substate.Output.ToArray(), logs, stateRoot);
                }
            }

            return substate.EvmExceptionType != EvmExceptionType.None
                ? TransactionResult.EvmException(substate.EvmExceptionType, substate.SubstateError)
                : TransactionResult.Ok;
        }

        protected virtual TransactionResult CalculateAvailableGas(Transaction tx, IReleaseSpec spec, in IntrinsicGas<TGasPolicy> intrinsicGas, out TGasPolicy gasAvailable)
        {
            gasAvailable = TGasPolicy.CreateAvailableFromIntrinsic(tx.GasLimit, intrinsicGas.Standard, spec);
            return TransactionResult.Ok;
        }

        private TransactionResult Apply8037DelegationRefunds(
            Transaction tx,
            IReleaseSpec spec,
            in IntrinsicGas<TGasPolicy> intrinsicGas,
            ref TGasPolicy gasAvailable,
            ref long delegationRefunds,
            ref long delegationAuthBaseRefunds)
        {
            if (spec.IsEip8037Enabled && (delegationRefunds > 0 || delegationAuthBaseRefunds > 0))
            {
                TGasPolicy intrinsicGasStandard = intrinsicGas.Standard;
                long stateGasFloor = TGasPolicy.GetStateReservoir(in intrinsicGasStandard);
                long newAccountStateCost = TGasPolicy.GetNewAccountStateCost();
                long perAuthBaseStateCost = TGasPolicy.GetPerAuthBaseStateCost();
                bool refundWithinBounds = TryCalculate8037DelegationRefund(
                    newAccountStateCost,
                    perAuthBaseStateCost,
                    delegationRefunds,
                    delegationAuthBaseRefunds,
                    out long stateGasRefund);
                Debug.Assert(refundWithinBounds, "Authorization refunds are bounded before delegation processing.");
                if (!refundWithinBounds)
                {
                    TraceLogInvalidTx(tx, "AUTHORIZATION_REFUND_OVERFLOW");
                    return TransactionResult.ErrorType.MalformedTransaction.WithDetail("authorization refund gas overflow");
                }

                long refundFloor = Math.Max(0, stateGasFloor - stateGasRefund);
                TGasPolicy.RefundStateGas(ref gasAvailable, stateGasRefund, refundFloor, trackSpillRefund: false);
                // delegationRefunds is intentionally NOT zeroed: it flows on to Refund as codeInsertRefunds
                // for the regular ACCOUNT_WRITE refund; the state-dimension refund was applied just above.
                delegationAuthBaseRefunds = 0;
            }

            return TransactionResult.Ok;
        }

        private TransactionResult Validate8037DelegationRefundBounds(Transaction tx, IReleaseSpec spec, in TGasPolicy gasAvailable)
        {
            if (!spec.IsEip8037Enabled || !tx.HasAuthorizationList)
            {
                return TransactionResult.Ok;
            }

            long newAccountStateCost = TGasPolicy.GetNewAccountStateCost();
            long perAuthBaseStateCost = TGasPolicy.GetPerAuthBaseStateCost();
            long maxRefunds = tx.AuthorizationList.Length;
            if (!TryCalculate8037DelegationRefund(
                    newAccountStateCost,
                    perAuthBaseStateCost,
                    maxRefunds,
                    maxRefunds,
                    out _))
            {
                TraceLogInvalidTx(tx, "AUTHORIZATION_REFUND_OVERFLOW");
                return TransactionResult.ErrorType.MalformedTransaction.WithDetail("authorization refund gas overflow");
            }

            return TransactionResult.Ok;
        }

        private static bool TryCalculate8037DelegationRefund(
            long newAccountStateCost,
            long perAuthBaseStateCost,
            long delegationRefunds,
            long delegationAuthBaseRefunds,
            out long stateGasRefund)
        {
            stateGasRefund = 0;
            if (newAccountStateCost < 0 ||
                perAuthBaseStateCost < 0 ||
                delegationRefunds < 0 ||
                delegationAuthBaseRefunds < 0)
            {
                return false;
            }

            if ((delegationRefunds != 0 && newAccountStateCost > long.MaxValue / delegationRefunds) ||
                (delegationAuthBaseRefunds != 0 && perAuthBaseStateCost > long.MaxValue / delegationAuthBaseRefunds))
            {
                return false;
            }

            long newAccountStateRefund = newAccountStateCost * delegationRefunds;
            long authBaseStateRefund = perAuthBaseStateCost * delegationAuthBaseRefunds;
            if (newAccountStateRefund > long.MaxValue - authBaseStateRefund)
            {
                return false;
            }

            stateGasRefund = newAccountStateRefund + authBaseStateRefund;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private long ProcessDelegations(Transaction tx, IReleaseSpec spec, in StackAccessTracker accessTracker, out long authBaseRefunds)
        {
            Debug.Assert(spec.IsEip7702Enabled && tx.HasAuthorizationList);

            long refunds = 0;
            authBaseRefunds = 0;
            // Tracks each authority's delegation status as of tx start, so AUTH_BASE refills can tell
            // a pre-existing delegation from one installed earlier in the same tx.
            Dictionary<Address, bool>? delegatedBeforeTx = null;
            foreach (AuthorizationTuple authTuple in tx.AuthorizationList)
            {
                Address authority = (authTuple.Authority ??= Ecdsa.RecoverAddress(authTuple))!;

                AuthorizationTupleResult authorizationResult = IsValidForExecution(authTuple, accessTracker, spec, out bool hasDelegation, out string? error);
                if (authorizationResult != AuthorizationTupleResult.Valid)
                {
                    if (Logger.IsDebug) Logger.Debug($"Delegation {authTuple} is invalid with error: {error}");
                    // An invalid authorization touches no state, so its worst-case intrinsic charges are refunded.
                    if (spec.IsEip8037Enabled)
                    {
                        refunds++;
                        authBaseRefunds++;
                    }
                }
                else
                {
                    bool accountExists = WorldState.AccountExists(authority);
                    bool clearsDelegation = authTuple.CodeAddress == Address.Zero;

                    delegatedBeforeTx ??= [];
                    if (!delegatedBeforeTx.TryGetValue(authority, out bool delegatedBefore))
                    {
                        delegatedBefore = hasDelegation;
                        delegatedBeforeTx[authority] = delegatedBefore;
                    }

                    if (!accountExists)
                    {
                        WorldState.CreateAccount(authority, 0, 1);
                    }
                    else
                    {
                        refunds++;
                        WorldState.IncrementNonce(authority);
                    }

                    // AUTH_BASE refill: clearing always refills, twice when installed earlier in THIS tx;
                    // setting refills when the slot holds a delegation now or at tx start.
                    if (clearsDelegation)
                    {
                        authBaseRefunds++;
                        if (hasDelegation && !delegatedBefore) authBaseRefunds++;
                    }
                    else if (hasDelegation || delegatedBefore)
                    {
                        authBaseRefunds++;
                    }

                    _codeInfoRepository.SetDelegation(authTuple.CodeAddress, authority, spec);
                }
            }

            return refunds;
        }

        private enum AuthorizationTupleResult
        {
            Valid,
            IncorrectNonce,
            InvalidNonce,
            InvalidChainId,
            InvalidSignature,
            InvalidAsCodeDeployed
        }

        private AuthorizationTupleResult IsValidForExecution(
            AuthorizationTuple authorizationTuple,
            in StackAccessTracker accessTracker,
            IReleaseSpec spec,
            out bool hasDelegation,
            [NotNullWhen(false)] out string? error)
        {
            hasDelegation = false;
            if (authorizationTuple.ChainId != 0 && SpecProvider.ChainId != authorizationTuple.ChainId)
            {
                error = $"Chain id ({authorizationTuple.ChainId}) does not match.";
                return AuthorizationTupleResult.InvalidChainId;
            }

            if (authorizationTuple.Nonce == ulong.MaxValue)
            {
                error = $"Nonce ({authorizationTuple.Nonce}) must be less than 2**64 - 1.";
                return AuthorizationTupleResult.InvalidNonce;
            }

            UInt256 s = new(authorizationTuple.AuthoritySignature.SAsSpan, isBigEndian: true);
            if (authorizationTuple.Authority is null
                || s > SecP256k1Curve.HalfN
                //V minus the offset can only be 1 or 0 since eip-155 does not apply to Setcode signatures
                || authorizationTuple.AuthoritySignature.V - Signature.VOffset > 1)
            {
                error = "Bad signature.";
                return AuthorizationTupleResult.InvalidSignature;
            }

            accessTracker.WarmUp(authorizationTuple.Authority);

            if (WorldState.HasCode(authorizationTuple.Authority))
            {
                hasDelegation = _codeInfoRepository.TryGetDelegation(authorizationTuple.Authority, spec, out _);
                if (!hasDelegation)
                {
                    error = $"Authority ({authorizationTuple.Authority}) has code deployed.";
                    return AuthorizationTupleResult.InvalidAsCodeDeployed;
                }
            }

            ulong authNonce = WorldState.GetNonce(authorizationTuple.Authority);
            if (authNonce != authorizationTuple.Nonce)
            {
                error = $"Skipping tuple in authorization_list because nonce is set to {authorizationTuple.Nonce}, but authority ({authorizationTuple.Authority}) has {authNonce}.";
                return AuthorizationTupleResult.IncorrectNonce;
            }

            error = null;
            return AuthorizationTupleResult.Valid;
        }

        protected virtual IReleaseSpec GetSpec(BlockHeader header) => VirtualMachine.BlockExecutionContext.Spec;

        private static void UpdateMetrics(ExecutionOptions opts, UInt256 effectiveGasPrice)
        {
            // Block production (BuildUp) is excluded: payload builds run concurrently with block import
            // on the same process-wide gas aggregates, and the gauges describe imported blocks, not candidates.
            if (opts is ExecutionOptions.Commit or ExecutionOptions.None)
            {
                Metrics.UpdateBlockGasPrice(effectiveGasPrice);
            }
        }

        /// <summary>
        /// Validates the transaction, in a static manner (i.e. without accessing state/storage).
        /// It basically ensures the transaction is well formed (i.e. no null values where not allowed, no overflows, etc).
        /// As a part of validating the transaction the premium per gas will be calculated, to save computation this
        /// is returned in an out parameter.
        /// </summary>
        /// <param name="tx">The transaction to validate</param>
        /// <param name="header">The block containing the transaction. Only BaseFee is being used from the block atm.</param>
        /// <param name="spec">The release spec with which the transaction will be executed</param>
        /// <param name="opts">Options (Flags) to use for execution</param>
        /// <param name="intrinsicGas">Calculated intrinsic gas</param>
        /// <returns></returns>
        protected virtual TransactionResult ValidateStatic(
            Transaction tx,
            BlockHeader header,
            IReleaseSpec spec,
            ExecutionOptions opts,
            in IntrinsicGas<TGasPolicy> intrinsicGas)
        {

            bool validate = !opts.HasFlag(ExecutionOptions.SkipValidation);

            if (tx.SenderAddress is null)
            {
                TraceLogInvalidTx(tx, "SENDER_NOT_SPECIFIED");
                return TransactionResult.SenderNotSpecified;
            }

            if (validate && tx.Nonce >= ulong.MaxValue - 1)
            {
                // we are here if nonce is at least (ulong.MaxValue - 1). If tx is contract creation,
                // it is max possible value. Otherwise, (ulong.MaxValue - 1) is allowed, but ulong.MaxValue not.
                if (tx.IsContractCreation || tx.Nonce == ulong.MaxValue)
                {
                    TraceLogInvalidTx(tx, "NONCE_OVERFLOW");
                    return TransactionResult.NonceOverflow;
                }
            }

            if (tx.IsAboveInitCode(spec))
            {
                TraceLogInvalidTx(tx, $"CREATE_TRANSACTION_SIZE_EXCEEDS_MAX_INIT_CODE_SIZE {tx.DataLength} > {spec.MaxInitCodeSize}");
                return TransactionResult.TransactionSizeOverMaxInitCodeSize;
            }

            if (tx.SupportsAuthorizationList)
            {
                ValidationResult noCreation = SetCodeTxValidation.ValidateNoContractCreation(tx);
                if (!noCreation)
                {
                    TraceLogInvalidTx(tx, "SETCODE_TX_CREATE");
                    return TransactionResult.ErrorType.MalformedTransaction.WithDetail($"{noCreation.Error} (sender {tx.SenderAddress})");
                }

                ValidationResult authList = SetCodeTxValidation.ValidateAuthorizationList(tx);
                if (!authList)
                {
                    TraceLogInvalidTx(tx, "EMPTY_AUTHORIZATION_LIST");
                    return TransactionResult.ErrorType.MalformedTransaction.WithDetail($"{authList.Error} (sender {tx.SenderAddress})");
                }
            }

            if (spec.IsEip8037Enabled && intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out ulong regular, out ulong floor))
            {
                TraceLogInvalidTx(tx, $"TX_INTRINSIC_GAS_EXCEEDS_CAP regular={regular} floor={floor} > {Eip7825Constants.DefaultTxGasLimitCap}");
                return TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail(
                    TxErrorMessages.TxIntrinsicGasExceedsCap(regular, floor, Eip7825Constants.DefaultTxGasLimitCap));
            }

            TGasPolicy standard = intrinsicGas.Standard;
            TGasPolicy minimal = intrinsicGas.MinimalGas;
            TGasPolicy floorGas = intrinsicGas.FloorGas;

            ulong standardGasUsed = TGasPolicy.GetRemainingGas(in standard);
            ulong floorGasUsed = TGasPolicy.GetRemainingGas(in floorGas);

            if (tx.GasLimit < standardGasUsed)
            {
                TraceLogInvalidTx(tx, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {tx.GasLimit} < {standardGasUsed}");
                return TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail(
                    $"{TxErrorMessages.IntrinsicGasTooLow}: have {tx.GasLimit}, want {standardGasUsed}");
            }

            if (tx.GasLimit < floorGasUsed)
            {
                TraceLogInvalidTx(tx, $"GAS_LIMIT_BELOW_FLOOR_DATA_GAS {tx.GasLimit} < {floorGasUsed}");
                return TransactionResult.ErrorType.GasLimitBelowFloorGas.WithDetail(
                    $"{TxErrorMessages.GasBelowFloorDataCost}: have {tx.GasLimit}, want {floorGasUsed}");
            }

            ulong minGasRequired = spec.IsEip8037Enabled
                ? Math.Max(TGasPolicy.GetRemainingGas(in standard) + (ulong)TGasPolicy.GetStateReservoir(in standard), TGasPolicy.GetRemainingGas(in minimal))
                : TGasPolicy.GetRemainingGas(in minimal);

            return ValidateGas(tx, header, spec, in standard, minGasRequired, validate);
        }

        protected virtual TransactionResult ValidateGas(Transaction tx, BlockHeader header, IReleaseSpec spec, in TGasPolicy intrinsicGas, ulong minGasRequired, bool validate)
        {
            if (tx.GasLimit < minGasRequired)
            {
                TraceLogInvalidTx(tx, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {tx.GasLimit} < {minGasRequired}");
                return TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail($"intrinsic gas too low: have {tx.GasLimit}, want {minGasRequired}");
            }

            if (validate)
            {
                if (spec.IsEip8037Enabled)
                {
                    if (tx.GasLimit > header.GasLimit)
                    {
                        TraceLogInvalidTx(tx, $"BLOCK_GAS_LIMIT_EXCEEDED {tx.GasLimit} > {header.GasLimit}");
                        return TransactionResult.BlockGasLimitExceeded;
                    }

                    // Per-block EIP-8037 inclusion depends on cumulative regular/state gas,
                    // so block validation performs the 2D check in BlockAccessListManager
                    // where those accumulators are available. Direct Execute/BuildUp/estimator
                    // callers can only validate the tx-local allowance here.
                    return TransactionResult.Ok;
                }

                // Admission must use the same basis as block accounting (header.GasUsed): pre-refund under EIP-7778, post-refund otherwise.
                ulong gasUsedForAllowance = _parallel ? 0 : header.GasUsed;

                ulong maxTransactionGasLimit = header.GasLimit - gasUsedForAllowance;
                if (tx.GasLimit > maxTransactionGasLimit)
                {
                    string limitDescription = _parallel
                        ? $"{header.GasLimit}"
                        : $"{header.GasLimit} - {gasUsedForAllowance}";
                    TraceLogInvalidTx(tx, $"BLOCK_GAS_LIMIT_EXCEEDED {tx.GasLimit} > {limitDescription}");
                    return TransactionResult.BlockGasLimitExceeded;
                }
            }

            return TransactionResult.Ok;
        }

        protected virtual bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
        {
            bool deleteCallerAccount = false;
            Address? sender = tx.SenderAddress;
            if (sender is null || !WorldState.AccountExists(sender))
            {
                bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;
                bool restore = opts.HasFlag(ExecutionOptions.Restore);
                bool noValidation = opts.HasFlag(ExecutionOptions.SkipValidation);

                if (Logger.IsDebug) Logger.Debug($"TX sender account does not exist {sender} - trying to recover it");

                // hacky fix for the potential recovery issue
                if (tx.Signature is not null)
                    tx.SenderAddress = Ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

                if (sender != tx.SenderAddress)
                {
                    if (Logger.IsWarn) Logger.Warn($"TX recovery issue fixed - tx was coming with sender {sender} and the now it recovers to {tx.SenderAddress}");
                    sender = tx.SenderAddress;
                }
                else
                {
                    TraceLogInvalidTx(tx, $"SENDER_ACCOUNT_DOES_NOT_EXIST {sender}");
                    if (!commit || noValidation || effectiveGasPrice.IsZero)
                    {
                        deleteCallerAccount = !commit || restore;
                        WorldState.CreateAccount(sender!, in UInt256.Zero);
                    }
                }

                if (sender is null)
                {
                    ThrowInvalidDataException($"Failed to recover sender address on tx {tx.Hash} when previously recovered sender account did not exist.");
                }
            }

            return deleteCallerAccount;
        }

        protected virtual IntrinsicGas<TGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
            => TGasPolicy.CalculateIntrinsicGas(tx, spec, blockGasLimit);

        protected virtual UInt256 CalculateEffectiveGasPrice(Transaction tx, bool eip1559Enabled, in UInt256 baseFee, out UInt256 opcodeGasPrice)
        {
            opcodeGasPrice = tx.CalculateEffectiveGasPrice(eip1559Enabled, in baseFee);
            return opcodeGasPrice;
        }

        protected virtual bool TryCalculatePremiumPerGas(Transaction tx, in UInt256 baseFee, out UInt256 premiumPerGas) =>
            tx.TryCalculatePremiumPerGas(baseFee, out premiumPerGas);

        protected virtual TransactionResult ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.SkipValidation);

            if (validate && WorldState.IsInvalidContractSender(spec, tx.SenderAddress!))
            {
                TraceLogInvalidTx(tx, "SENDER_IS_CONTRACT");
                return TransactionResult.SenderHasDeployedCode;
            }

            return TransactionResult.Ok;
        }

        protected static bool ShouldValidateGas(Transaction tx, ExecutionOptions opts)
            => !opts.HasFlag(ExecutionOptions.SkipValidation) || tx.MaxFeePerGas != 0UL || tx.MaxPriorityFeePerGas != 0UL;

        protected virtual TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
                in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
        {
            premiumPerGas = UInt256.Zero;
            senderReservedGasPayment = UInt256.Zero;
            blobBaseFee = UInt256.Zero;
            UInt256 balance = WorldState.GetBalance(tx.SenderAddress!);

            bool validate = ShouldValidateGas(tx, opts);

            BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
            if (validate && !TryCalculatePremiumPerGas(tx, header.BaseFeePerGas, out premiumPerGas))
            {
                TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
                string errorDetail = $"max fee per gas less than block base fee: address {tx.SenderAddress?.ToString(withEip55Checksum: true) ?? "unknown"}, maxFeePerGas: {tx.MaxFeePerGas}, baseFee: {header.BaseFeePerGas}";
                return TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee.WithDetail(errorDetail);
            }

            // mgval = gasLimit * effectiveGasPrice.
            if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out senderReservedGasPayment))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {balance}");
                return RequiredBalanceExceeds256Bits(tx);
            }

            // balanceCheck = gasLimit * MaxFeePerGas (EIP-1559) or mgval — the maximum the sender must hold.
            UInt256 balanceCheck;
            if (spec.IsEip1559Enabled && !tx.IsFree())
            {
                if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out balanceCheck))
                {
                    TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {balance}, MAX_FEE_PER_GAS: {tx.MaxFeePerGas}");
                    return RequiredBalanceExceeds256Bits(tx);
                }
            }
            else
            {
                balanceCheck = senderReservedGasPayment;
            }

            // Include tx.Value in the balance requirement.
            if (UInt256.AddOverflow(balanceCheck, tx.ValueRef, out balanceCheck))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {balance}");
                return RequiredBalanceExceeds256Bits(tx);
            }

            if (tx.SupportsBlobs)
            {
                UInt256 blobGas = BlobGasCalculator.CalculateBlobGas(tx);

                // Add blob fee cap to balance check.
                if (UInt256.MultiplyOverflow(blobGas, (UInt256)tx.MaxFeePerBlobGas!, out UInt256 maxBlobGasFee)
                    || UInt256.AddOverflow(balanceCheck, maxBlobGasFee, out balanceCheck))
                {
                    TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_BLOB_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {balance}");
                    return RequiredBalanceExceeds256Bits(tx);
                }

                // Compute actual blob fee and add to mgval.
                if (!_blobBaseFeeCalculator.TryCalculateBlobFees(header, tx, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas, out blobBaseFee))
                {
                    TraceLogInvalidTx(tx, $"BLOB_BASE_FEE_OVERFLOW: ({tx.SenderAddress})_BALANCE = {balance}");
                    return RequiredBalanceExceeds256Bits(tx);
                }

                if (tx.MaxFeePerBlobGas < feePerBlobGas)
                {
                    TraceLogInvalidTx(tx, "INSUFFICIENT_MAX_FEE_PER_BLOB_GAS");
                    return TransactionResult.WithDetail(TransactionResult.ErrorType.InsufficientSenderBalance, BlockErrorMessages.InsufficientMaxFeePerBlobGas(tx.SenderAddress, tx.MaxFeePerBlobGas, feePerBlobGas));
                }

                if (UInt256.AddOverflow(senderReservedGasPayment, blobBaseFee, out senderReservedGasPayment))
                {
                    TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {balance}");
                    return RequiredBalanceExceeds256Bits(tx);
                }
            }

            if (balance < balanceCheck)
            {
                // A warm sender may be funded earlier in the block by another sender's
                // transaction, which per-sender warm groups cannot see; charge best-effort
                // instead of losing that sender's warming entirely.
                if (opts.HasFlag(ExecutionOptions.Warmup))
                {
                    UInt256 warmCharge = UInt256.Min(senderReservedGasPayment, balance);
                    if (!warmCharge.IsZero) WorldState.SubtractFromBalance(tx.SenderAddress, warmCharge, spec);
                    return TransactionResult.Ok;
                }

                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {balance}");
                return InsufficientFundsForGas(tx, balance, balanceCheck);
            }

            if (!senderReservedGasPayment.IsZero) WorldState.SubtractFromBalance(tx.SenderAddress, senderReservedGasPayment, spec);

            return TransactionResult.Ok;
        }

        private static TransactionResult RequiredBalanceExceeds256Bits(Transaction tx) =>
            TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance.WithDetail(
                $"{TxErrorMessages.InsufficientFundsForGas}: address {tx.SenderAddress?.ToString(withEip55Checksum: true)} required balance exceeds 256 bits");

        private static TransactionResult InsufficientFundsForGas(Transaction tx, UInt256 senderBalance, UInt256 want) =>
            TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance.WithDetail(
                $"{TxErrorMessages.InsufficientFundsForGas}: address {tx.SenderAddress?.ToString(withEip55Checksum: true)} have {senderBalance} want {want}");

        protected virtual TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.SkipValidation);
            ulong nonce = WorldState.GetNonce(tx.SenderAddress!);
            if (validate && tx.Nonce != nonce)
            {
                TraceLogInvalidTx(tx, $"WRONG_TRANSACTION_NONCE: {tx.Nonce} (expected {nonce})");
                // Geth core/state_transition.go ErrNonceTooHigh / ErrNonceTooLow.
                string sender = tx.SenderAddress?.ToString(withEip55Checksum: true) ?? "unknown";
                return tx.Nonce > nonce
                    ? TransactionResult.ErrorType.TransactionNonceTooHigh.WithDetail(
                        $"nonce too high: address {sender}, tx: {tx.Nonce} state: {nonce}")
                    : TransactionResult.ErrorType.TransactionNonceTooLow.WithDetail(
                        $"nonce too low: address {sender}, tx: {tx.Nonce} state: {nonce}");
            }

            ulong newNonce = validate || nonce < ulong.MaxValue ? nonce + 1 : 0;
            WorldState.SetNonce(tx.SenderAddress, newNonce);

            return TransactionResult.Ok;
        }

        protected virtual void DecrementNonce(Transaction tx) => WorldState.DecrementNonce(tx.SenderAddress!);

        [SkipLocalsInit]
        private TransactionResult BuildExecutionEnvironment(
            Transaction tx,
            IReleaseSpec spec,
            ICodeInfoRepository codeInfoRepository,
            in StackAccessTracker accessTracker,
            CodeInfo? preloadedCodeInfo,
            Address? preloadedDelegationAddress,
            out ExecutionEnvironment env)
        {
            Address recipient = tx.GetRecipient(tx.IsContractCreation ? WorldState.GetNonce(tx.SenderAddress!) : 0);
            if (recipient is null) ThrowInvalidDataException("Recipient has not been resolved properly before tx execution");
            CodeInfo? codeInfo;
            ReadOnlyMemory<byte> inputData = tx.IsMessageCall ? tx.Data : default;
            if (tx.IsContractCreation)
            {
                codeInfo = CodeInfoFactory.CreateCodeInfo(tx.Data);
            }
            else
            {
                Address? delegationAddress;
                if (preloadedCodeInfo is not null)
                {
                    codeInfo = preloadedCodeInfo;
                    delegationAddress = preloadedDelegationAddress;
                }
                else
                {
                    codeInfo = codeInfoRepository.GetCachedCodeInfo(recipient, spec, out delegationAddress);
                }

                //We assume eip-7702 must be active if it is a delegation
                if (delegationAddress is not null)
                    accessTracker.WarmUp(delegationAddress);
            }

            WarmUpTxAccesses(tx, spec, in accessTracker, recipient);

            env = ExecutionEnvironment.Rent(
                codeInfo: codeInfo,
                executingAccount: recipient,
                caller: tx.SenderAddress!,
                codeSource: recipient,
                callDepth: 0,
                value: in tx.ValueRef,
                inputData: in inputData);

            return TransactionResult.Ok;
        }

        protected virtual bool ShouldValidate(ExecutionOptions opts) => !opts.HasFlag(ExecutionOptions.SkipValidation);

        private int ExecuteEvmCall<TTracingInst>(
            Transaction tx,
            BlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            ExecutionOptions opts,
            long delegationRefunds,
            IntrinsicGas<TGasPolicy> gas,
            in StackAccessTracker accessedItems,
            TGasPolicy gasAvailable,
            ExecutionEnvironment env,
            bool topFrameOutOfGas,
            out TransactionSubstate substate,
            out GasConsumed gasConsumed)
            where TTracingInst : struct, IFlag
        {
            substate = default;
            gasConsumed = tx.GasLimit;
            byte statusCode = StatusCode.Failure;

            // EIP-7702 + EIP-8037: capture the tx-start state reservoir after authorization refunds.
            // The halt path needs this to correctly initialize the reservoir in ResetForHalt.
            long postIntrinsicStateReservoir = TGasPolicy.GetStateReservoir(in gasAvailable);

            Snapshot snapshot = WorldState.TakeSnapshot();
            ulong floorGasLong = TGasPolicy.GetRemainingGas(gas.FloorGas);

            // EIP-8037: a successful create to a pre-existing (alive) account refunds the intrinsic
            // NEW_ACCOUNT state gas — no new account leaf is materialised.
            bool createdTargetAlive = tx.IsContractCreation && !WorldState.IsDeadAccount(env.ExecutingAccount);

            if (topFrameOutOfGas)
            {
                TGasPolicy.SetOutOfGas(ref gasAvailable);
                TGasPolicy oogIntrinsicGasStandard = gas.Standard;
                gasConsumed = CompleteEip8037Halt(tx, spec, opts, ref gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, in oogIntrinsicGasStandard, floorGasLong, postIntrinsicStateReservoir);
                goto Complete;
            }

            PayValue(tx, spec, opts);

            if (env.CodeInfo is not null)
            {
                if (tx.IsContractCreation)
                {
                    // if transaction is a contract creation then recipient address is the contract deployment address
                    if (!PrepareDeployment(env.ExecutingAccount))
                    {
                        if (Logger.IsTrace) Logger.Trace("Restoring state from before transaction");
                        WorldState.Restore(snapshot);
                        TGasPolicy collisionIntrinsicGasStandard = gas.Standard;
                        gasConsumed = RefundOnContractCollision(
                            tx,
                            spec,
                            opts,
                            in gasAvailable,
                            VirtualMachine.TxExecutionContext.GasPrice,
                            in collisionIntrinsicGasStandard,
                            floorGasLong);
                        goto Complete;
                    }
                }
            }
            else
            {
                // Gas for initcode execution is not consumed, only intrinsic creation transaction costs are charged.
                ulong minimalGasLong = TGasPolicy.GetRemainingGas(gas.MinimalGas);
                gasConsumed = minimalGasLong;
                // If noValidation we didn't charge for gas, so do not refund; otherwise return unspent gas
                if (!opts.HasFlag(ExecutionOptions.SkipValidation))
                    WorldState.AddToBalance(tx.SenderAddress!, (tx.GasLimit - minimalGasLong) * VirtualMachine.TxExecutionContext.GasPrice, spec);
                goto Complete;
            }

            ExecutionType executionType = tx.IsContractCreation ? ExecutionType.CREATE : ExecutionType.TRANSACTION;

            using (VmState<TGasPolicy> state = VmState<TGasPolicy>.RentTopLevel(gasAvailable, executionType, env, in accessedItems, in snapshot))
            {
                substate = !TTracingInst.IsActive
                    ? VirtualMachine.ExecuteTransaction(state, WorldState, tracer) // no GVM trick for ZK
                    : VirtualMachine.ExecuteTransaction<OnFlag>(state, WorldState, tracer);

                Metrics.IncrementOpCodes(VirtualMachine.OpCodeCount);
                VirtualMachine.FlushMetricsCounters();
                gasAvailable = state.Gas;

                if (tracer.IsTracingAccess)
                {
                    tracer.ReportAccess(accessedItems.AccessedAddresses, accessedItems.AccessedStorageCells);
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (Logger.IsTrace) Logger.Trace("Restoring state from before transaction");
                    WorldState.Restore(snapshot);
                }
                else
                {
                    if (tx.IsContractCreation)
                    {
                        if (!DeployContract(spec, env.ExecutingAccount, in substate, in accessedItems, ref gasAvailable))
                        {
                            goto FailContractCreate;
                        }
                    }

                    // EIP-8037: defer destroy list processing to after PayFees so that
                    // burn logs include the priority fee in the balance.
                    bool deferFinalization = spec.IsEip7708Enabled && spec.IsEip8037Enabled;
                    JournalSet<Address>? destroyList = substate.DestroyList;
                    if (!deferFinalization && destroyList?.Count > 0)
                    {
                        // Same derivation as Execute: !commit = build-up round spanning the block.
                        bool commit = opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) && !spec.IsEip658Enabled);
                        bool eip7708Enabled = spec.IsEip7708Enabled;
                        bool removeSelfdestructBurn = spec.IsEip8246Enabled;
                        bool tracingRefunds = tracer.IsTracingRefunds;
                        foreach (Address toBeDestroyed in destroyList)
                        {
                            if (Logger.IsTrace) Logger.Trace($"Destroying account {toBeDestroyed}");

                            UInt256 balance = eip7708Enabled || removeSelfdestructBurn ? WorldState.GetBalance(toBeDestroyed) : default;

                            // EIP-7708 logs the burn; suppressed once EIP-8246 stops burning.
                            if (eip7708Enabled && !removeSelfdestructBurn && !balance.IsZero)
                            {
                                substate.Logs.Add(TransferLog.CreateSelfDestruct(toBeDestroyed, balance));
                            }

                            DestroyAccount(WorldState, toBeDestroyed, in balance, commit, removeSelfdestructBurn);

                            if (tracingRefunds)
                            {
                                tracer.ReportRefund((long)spec.GasCosts.DestroyRefund);
                            }
                        }
                    }

                    statusCode = StatusCode.Success;
                }
            }

            gasConsumed = Refund(tx, header, spec, opts, in substate, gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, (ulong)delegationRefunds, gas.FloorGas, gas.Standard, postIntrinsicStateReservoir, createdTargetAlive);
            goto Complete;
        FailContractCreate:
            if (Logger.IsTrace) Logger.Trace("Restoring state from before transaction");
            if (spec.ChargeForTopLevelCreate)
            {
                TGasPolicy.SetOutOfGas(ref gasAvailable);
            }
            WorldState.Restore(snapshot);
            TGasPolicy intrinsicGasStandard = gas.Standard;
            if (spec.IsEip8037Enabled)
            {
                // Use postIntrinsicStateReservoir captured before EVM execution so any
                // EIP-7702 auth refund applied via Apply8037DelegationRefunds is preserved
                // (otherwise the sender pays AccountCreationCost per refunded auth even
                // though no account was created).
                gasConsumed = CompleteEip8037Halt(tx, spec, opts, ref gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, in intrinsicGasStandard, floorGasLong, postIntrinsicStateReservoir);
            }
            else
            {
                gasConsumed = RefundOnFail(tx, spec, opts, in gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, in intrinsicGasStandard, floorGasLong);
            }
        Complete:
            return statusCode;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void RefundRevertedExecutionStateGas(IReleaseSpec spec, long stateGasFloor, ref TGasPolicy gas)
        {
            if (!spec.IsEip8037Enabled)
            {
                return;
            }

            long revertedStateGas = TGasPolicy.GetStateGasUsed(in gas);
            if (revertedStateGas > stateGasFloor)
            {
                TGasPolicy.RefundStateGas(ref gas, revertedStateGas, stateGasFloor);
            }
        }

        // Common EIP-8037 halt-prepare-then-restore sequence shared by FailContractCreate
        // and the substate.IsError branch of Refund. AggressiveInlining keeps codegen
        // identical to the prior inline form on both hot paths.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private GasConsumed CompleteEip8037Halt(
            Transaction tx,
            IReleaseSpec spec,
            ExecutionOptions opts,
            ref TGasPolicy gas,
            in UInt256 gasPrice,
            in TGasPolicy intrinsicGasStandard,
            ulong floorGas,
            long postIntrinsicStateReservoir,
            ulong codeInsertRegularRefund = 0)
        {
            long intrinsicStateGas = TGasPolicy.GetStateReservoir(in intrinsicGasStandard);
            long refundedTopLevelCreateStateGas = CalculateTopLevelCreateIntrinsicStateRefund(tx, in intrinsicGasStandard);
            long initialStateReservoir = CalculateInitialStateReservoir((long)tx.GasLimit, in intrinsicGasStandard);
            long refundedIntrinsicStateGas = Math.Max(0, postIntrinsicStateReservoir - initialStateReservoir) + refundedTopLevelCreateStateGas;
            long postHaltIntrinsicStateGas = Math.Max(0, intrinsicStateGas - refundedIntrinsicStateGas);
            if (refundedTopLevelCreateStateGas > 0)
            {
                long topLevelCreateStateGasFloor = intrinsicStateGas - refundedTopLevelCreateStateGas;
                TGasPolicy.RefundStateGas(ref gas, refundedTopLevelCreateStateGas, topLevelCreateStateGasFloor, trackSpillRefund: false);
            }

            RefundRevertedExecutionStateGas(spec, postHaltIntrinsicStateGas, ref gas);
            long postHaltStateReservoir = Math.Max(postIntrinsicStateReservoir, TGasPolicy.GetStateReservoir(in gas));
            if (refundedTopLevelCreateStateGas > 0)
            {
                postHaltStateReservoir = Math.Max(postHaltStateReservoir, refundedTopLevelCreateStateGas);
            }

            TGasPolicy.ResetForHalt(ref gas, postHaltStateReservoir, postHaltIntrinsicStateGas);
            return RefundOnTopLevelHalt(tx, spec, opts, in gas, in gasPrice, in intrinsicGasStandard, floorGas, codeInsertRegularRefund);
        }

        protected virtual GasConsumed RefundOnFail(
            Transaction tx,
            IReleaseSpec spec,
            ExecutionOptions opts,
            in TGasPolicy gas,
            in UInt256 gasPrice,
            in TGasPolicy intrinsicGasStandard,
            ulong floorGas = 0)
        {
            if (!spec.IsEip8037Enabled)
                return tx.GasLimit;

            ulong preRefundGas = TGasPolicy.GetPreRefundGas(in gas, tx.GasLimit);
            ulong spentGas = Math.Max(preRefundGas, floorGas);
            long blockStateGas = TGasPolicy.GetStateGasUsed(in gas);
            Debug.Assert(blockStateGas >= 0, $"EIP-8037 fail-path invariant violated: negative block state gas ({blockStateGas}).");
            ulong blockGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(preRefundGas, (ulong)blockStateGas);

            return RefundFailedEip8037Gas(tx, spec, opts, in gasPrice, spentGas, blockGas, blockStateGas);
        }

        // Keep available for override for Arbitrum plugin needs
        protected virtual GasConsumed RefundOnContractCollision(
            Transaction tx,
            IReleaseSpec spec,
            ExecutionOptions opts,
            in TGasPolicy gas,
            in UInt256 gasPrice,
            in TGasPolicy intrinsicGasStandard,
            ulong floorGas)
        {
            if (!spec.IsEip8037Enabled)
                return tx.GasLimit;

            TGasPolicy gasAfterCollision = gas;
            long refundedTopLevelCreateStateGas = CalculateTopLevelCreateIntrinsicStateRefund(tx, in intrinsicGasStandard);
            if (refundedTopLevelCreateStateGas > 0)
            {
                long stateGasFloor = TGasPolicy.GetStateReservoir(in intrinsicGasStandard) - refundedTopLevelCreateStateGas;
                TGasPolicy.RefundStateGas(ref gasAfterCollision, refundedTopLevelCreateStateGas, stateGasFloor, trackSpillRefund: false);
            }

            TGasPolicy.SetOutOfGas(ref gasAfterCollision);
            ulong spentGas = Math.Max(TGasPolicy.GetPreRefundGas(in gasAfterCollision, tx.GasLimit), floorGas);
            long blockStateGas = TGasPolicy.GetStateGasUsed(in gasAfterCollision);

            return RefundFailedEip8037Gas(tx, spec, opts, in gasPrice, spentGas, spentGas, blockStateGas);
        }

        protected virtual GasConsumed RefundOnTopLevelHalt(
            Transaction tx,
            IReleaseSpec spec,
            ExecutionOptions opts,
            in TGasPolicy gas,
            in UInt256 gasPrice,
            in TGasPolicy intrinsicGasStandard,
            ulong floorGas,
            ulong codeInsertRegularRefund = 0)
        {
            if (!spec.IsEip8037Enabled)
                return tx.GasLimit;

            long stateReservoir = TGasPolicy.GetStateReservoir(in gas);
            Debug.Assert(stateReservoir >= 0 && (ulong)stateReservoir <= tx.GasLimit,
                $"EIP-8037 halt-path invariant violated: reservoir ({stateReservoir}) exceeds gasLimit ({tx.GasLimit}).");
            // tx_gas_used_before_refund = tx.gas - gas_left - state_gas_left. The halt burns anything
            // left in gas_left (including refunded spill), so only the reservoir goes unspent here.
            ulong preRefundGas = tx.GasLimit - (ulong)stateReservoir;
            // The regular gas refund (e.g. EIP-7702 ACCOUNT_WRITE) survives a halt: the spec adds it to
            // the refund counter pre-execution and applies min(before_refund / 5, counter) to tx_gas_used.
            ulong regularRefund = CalculateClaimableRefund(preRefundGas, codeInsertRegularRefund, spec);
            ulong spentGas = Math.Max(preRefundGas - regularRefund, floorGas);
            long intrinsicStateGas = TGasPolicy.GetStateGasUsed(in gas);
            long spillBurned = TGasPolicy.GetStateGasSpillBurned(in gas);
            // On an exceptional halt spilled state gas ends up in gas_left and is burned as regular;
            // only the intrinsic state gas remaining after the reset stays in the state dimension.
            long effectiveStateGas = Math.Max(0, intrinsicStateGas - spillBurned);
            // Block regular gas = before_refund - state (tx_regular_gas); refunds and the calldata
            // floor adjust only the sender charge, never this dimension.
            Debug.Assert(tx.IsSystem() || (ulong)effectiveStateGas <= preRefundGas,
                $"EIP-8037 halt-path invariant violated: state gas ({effectiveStateGas}) exceeds pre-refund gas ({preRefundGas}).");
            // System txs are exempt from the assert above; saturate so their unused block gas cannot wrap.
            ulong blockGas = preRefundGas.SaturatingSub((ulong)effectiveStateGas);

            return RefundFailedEip8037Gas(tx, spec, opts, in gasPrice, spentGas, blockGas, effectiveStateGas);
        }

        private GasConsumed RefundFailedEip8037Gas(
            Transaction tx,
            IReleaseSpec spec,
            ExecutionOptions opts,
            in UInt256 gasPrice,
            ulong spentGas,
            ulong blockGas,
            long blockStateGas)
        {
            if (ShouldRefundGas(tx, opts, in gasPrice) && spentGas < tx.GasLimit)
                PayRefund(tx, (tx.GasLimit - spentGas) * gasPrice, spec);

            return new GasConsumed(spentGas, spentGas, blockGas, (ulong)blockStateGas, spentGas);
        }

        protected virtual bool DeployContract(IReleaseSpec spec, Address codeOwner, in TransactionSubstate substate, in StackAccessTracker accessedItems, ref TGasPolicy unspentGas)
        {
            if (!CodeDepositHandler.CalculateCost(spec, substate.Output.Length, in unspentGas, out ulong regularDepositCost, out long stateDepositCost))
                return false;

            if (CodeDepositHandler.CodeIsInvalid(spec, substate.Output))
                return false;

            // Copy the bytes so it's not live memory that will be used in another tx.
            return TryChargeCodeDeposit(spec, codeOwner, in accessedItems, ref unspentGas, regularDepositCost, stateDepositCost, substate.Output.ToArray());
        }

        private bool TryChargeCodeDeposit(
            IReleaseSpec spec,
            Address codeOwner,
            in StackAccessTracker accessedItems,
            ref TGasPolicy unspentGas,
            ulong regularDepositCost,
            long stateDepositCost,
            byte[] code)
        {
            ulong remainingGas = TGasPolicy.GetRemainingGas(in unspentGas);
            ulong stateSpill = TGasPolicy.CalculateStateGasSpill(in unspentGas, stateDepositCost);
            bool hasEnoughGas = remainingGas >= regularDepositCost
                && remainingGas - regularDepositCost >= stateSpill;

            if (!hasEnoughGas)
                return !spec.ChargeForTopLevelCreate;

            TGasPolicy gasAfterCodeDeposit = unspentGas;
            if (!TGasPolicy.TryConsumeStateAndRegularGas(ref gasAfterCodeDeposit, stateDepositCost, regularDepositCost))
                return false;

            _codeInfoRepository.InsertCode(code, codeOwner, spec);

            unspentGas = gasAfterCodeDeposit;
            return true;
        }

        protected virtual void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
        {
            if (tx.ValueRef.IsZero) return;

            // Same best-effort rule as BuyGas: a warm sender funded earlier in the block has no
            // parent-state balance to move, and failing here would abort its warming.
            if (opts.HasFlag(ExecutionOptions.Warmup))
            {
                UInt256 charge = UInt256.Min(tx.Value, WorldState.GetBalance(tx.SenderAddress!));
                if (!charge.IsZero) WorldState.SubtractFromBalance(tx.SenderAddress!, in charge, spec);
                return;
            }

            WorldState.SubtractFromBalance(tx.SenderAddress!, in tx.ValueRef, spec);
        }

        protected virtual void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, ulong spentGas, in UInt256 premiumPerGas, in UInt256 blobBaseFee, int statusCode)
        {
            UInt256 fees = premiumPerGas * spentGas;

            // n.b. destroyed accounts already set to zero balance
            // EIP-8037: always pay coinbase — deferred finalization will burn the balance
            bool gasBeneficiaryNotDestroyed = !substate.DestroyListContains(header.GasBeneficiary);
            if (statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed || spec.IsEip8037Enabled)
            {
                WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec);
            }

            UInt256 eip1559Fees = !tx.IsFree() ? header.BaseFeePerGas * spentGas : UInt256.Zero;
            UInt256 collectedFees = spec.IsEip1559Enabled ? eip1559Fees : UInt256.Zero;

            if (tx.SupportsBlobs && spec.IsEip4844FeeCollectorEnabled)
            {
                collectedFees += blobBaseFee;
            }

            if (spec.FeeCollector is not null && !collectedFees.IsZero)
            {
                WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, collectedFees, spec);
            }

            if (tracer.IsTracingFees)
            {
                tracer.ReportFees(fees, eip1559Fees + blobBaseFee);
            }
        }

        protected bool PrepareDeployment(Address contractAddress)
        {
            if (!WorldState.IsNonZeroAccount(contractAddress, out _))
                return true;

            if (Logger.IsTrace) Logger.Trace($"Contract collision at {contractAddress}");
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (Logger.IsTrace) Logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }

        protected virtual GasConsumed Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
            in TransactionSubstate substate, in TGasPolicy unspentGas, in UInt256 gasPrice, ulong codeInsertRefunds, in TGasPolicy floorGas, in TGasPolicy intrinsicGasStandard, long postIntrinsicStateReservoir, bool createdTargetAlive = false)
        {
            TGasPolicy gasAfterExecution = unspentGas;
            long stateGasFloor = TGasPolicy.GetStateReservoir(in intrinsicGasStandard);
            // EIP-8037: refund the top-level create's NEW_ACCOUNT state gas on revert or when the target
            // already existed; exceptional halts refund it via CompleteEip8037Halt instead.
            if (spec.IsEip8037Enabled && (substate.ShouldRevert || (!substate.IsError && createdTargetAlive)))
            {
                long refundedTopLevelCreateStateGas = CalculateTopLevelCreateIntrinsicStateRefund(tx, in intrinsicGasStandard);
                if (refundedTopLevelCreateStateGas > 0)
                {
                    stateGasFloor -= refundedTopLevelCreateStateGas;
                    TGasPolicy.RefundStateGas(ref gasAfterExecution, refundedTopLevelCreateStateGas, stateGasFloor, trackSpillRefund: false);
                }
            }

            ulong codeInsertRegularRefund = TGasPolicy.ApplyCodeInsertRefunds(ref gasAfterExecution, codeInsertRefunds, spec, stateGasFloor);
            ulong floorGasLong = TGasPolicy.GetRemainingGas(floorGas);

            if (substate.IsError && spec.IsEip8037Enabled)
            {
                // Use postIntrinsicStateReservoir captured before EVM execution so any
                // EIP-7702 auth refund applied via Apply8037DelegationRefunds is preserved
                // through the halt-reset.
                return CompleteEip8037Halt(tx, spec, opts, ref gasAfterExecution, in gasPrice, in intrinsicGasStandard, floorGasLong, postIntrinsicStateReservoir, codeInsertRegularRefund);
            }

            (ulong spentGas, long refund) = CalculateSpentGasAndRefund(tx, spec, in substate, in gasAfterExecution, codeInsertRegularRefund);
            (ulong blockGas, long blockStateGas) = CalculateBlockGas(spec, in gasAfterExecution, spentGas, floorGasLong);

            ulong operationGas = refund >= 0 ? spentGas - (ulong)refund : spentGas + (ulong)(-refund);
            ulong spentGasAfterFloor = Math.Max(operationGas, floorGasLong);

            if (ShouldRefundGas(tx, opts, in gasPrice))
                PayRefund(tx, (tx.GasLimit - spentGasAfterFloor) * gasPrice, spec);

            return new GasConsumed(spentGasAfterFloor, operationGas, blockGas, (ulong)blockStateGas, Math.Max(spentGas, floorGasLong));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long CalculateTopLevelCreateIntrinsicStateRefund(
            Transaction tx,
            in TGasPolicy intrinsicGasStandard)
        {
            if (!tx.IsContractCreation)
            {
                return 0;
            }

            return Math.Min(
                TGasPolicy.GetCreateStateCost(),
                TGasPolicy.GetStateReservoir(in intrinsicGasStandard));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long CalculateInitialStateReservoir(
            long txGasLimit,
            in TGasPolicy intrinsicGasStandard)
        {
            long intrinsicStateGas = TGasPolicy.GetStateReservoir(in intrinsicGasStandard);
            return Math.Max(0, txGasLimit - intrinsicStateGas - (long)Eip7825Constants.DefaultTxGasLimitCap);
        }

        private (ulong spentGas, long refund) CalculateSpentGasAndRefund(
            Transaction tx,
            IReleaseSpec spec,
            in TransactionSubstate substate,
            in TGasPolicy gasAfterExecution,
            ulong codeInsertRegularRefund)
        {
            ulong spentGas = substate.IsError
                ? tx.GasLimit
                : TGasPolicy.GetPreRefundGas(in gasAfterExecution, tx.GasLimit);

            long totalToRefund = (long)codeInsertRegularRefund;
            if (!substate.IsError && !substate.ShouldRevert)
                totalToRefund += substate.Refund + (substate.DestroyList?.Count ?? 0) * (long)spec.GasCosts.DestroyRefund;

            long quotient = spec.IsEip3529Enabled ? (long)RefundHelper.MaxRefundQuotientEIP3529 : (long)RefundHelper.MaxRefundQuotient;
            return (spentGas, Math.Min((long)(spentGas / (ulong)quotient), totalToRefund));
        }

        protected virtual ulong CalculateClaimableRefund(ulong spentGas, ulong totalRefund, IReleaseSpec spec)
            => RefundHelper.CalculateClaimableRefund(spentGas, totalRefund, spec);

        private static (ulong blockGas, long blockStateGas) CalculateBlockGas(
            IReleaseSpec spec,
            in TGasPolicy gasAfterExecution,
            ulong preRefundGas,
            ulong floorGas)
        {
            if (!spec.IsEip8037Enabled)
                return (spec.IsEip7778Enabled ? Math.Max(preRefundGas, floorGas) : 0, 0);

            long blockStateGas = TGasPolicy.GetStateGasUsed(in gasAfterExecution);
            Debug.Assert(blockStateGas >= 0, $"EIP-8037 invariant violated: negative block state gas ({blockStateGas}).");
            ulong blockGas = Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(preRefundGas, (ulong)blockStateGas);

            return (blockGas, blockStateGas);
        }

        protected virtual void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)
        {
            if (!refundAmount.IsZero)
                WorldState.AddToBalance(tx.SenderAddress!, refundAmount, spec);
        }

        private static bool ShouldRefundGas(Transaction tx, ExecutionOptions opts, in UInt256 gasPrice) =>
            !gasPrice.IsZero && ShouldValidateGas(tx, opts);

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidDataException(string message) => throw new InvalidDataException(message);

        // Devirtualised wrapper over Address.CompareTo (sealed -> already devirt'd inside) so the EIP-7708
        // destroy-list sort goes through Sort<TComparer> instead of Comparer<Address>.Default's virtual call.
        // The IComparer<Address> contract declares nullable parameters; the destroy-list source
        // (JournalSet<Address>) never contains null entries, so the `!` dereference is safe here.
        private readonly struct AddressByBytesComparer : IComparer<Address>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(Address? x, Address? y) => x!.CompareTo(y);
        }
    }

    /// <summary>
    /// Non-generic TransactionProcessorBase for backward compatibility with EthereumGasPolicy.
    /// </summary>
    public abstract class EthereumTransactionProcessorBase(
        ITransactionProcessor.IBlobBaseFeeCalculator? blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager,
        bool parallel = false)
        : TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);

    public readonly struct TransactionResult : IEquatable<TransactionResult>
    {
        private TransactionResult(ErrorType error = ErrorType.None, EvmExceptionType evmException = EvmExceptionType.None, string errorDescription = "")
        {
            Error = error;
            EvmExceptionType = evmException;
            ErrorDescription = errorDescription;
        }

        public ErrorType Error { get; }
        public bool TransactionExecuted => Error is ErrorType.None;
        public EvmExceptionType EvmExceptionType { get; }
        public string ErrorDescription { get; }

        public static implicit operator TransactionResult(ErrorType error) => new(error);
        public static implicit operator bool(TransactionResult result) => result.TransactionExecuted;
        public bool Equals(TransactionResult other) => (TransactionExecuted && other.TransactionExecuted) || (Error == other.Error);
        public static bool operator ==(TransactionResult obj1, TransactionResult obj2) => obj1.Equals(obj2);
        public static bool operator !=(TransactionResult obj1, TransactionResult obj2) => !obj1.Equals(obj2);
        public override bool Equals(object? obj) => obj is TransactionResult result && Equals(result);
        public override int GetHashCode() => TransactionExecuted ? 1 : Error.GetHashCode();
        public override string ToString() => Error is not ErrorType.None ? $"Fail : {ErrorDescription}" : "Success";

        public static TransactionResult EvmException(EvmExceptionType evmExceptionType, string? description = null) =>
            new(evmException: evmExceptionType, errorDescription: description ?? "");

        public static TransactionResult WithDetail(ErrorType errorType, string detail) => new(errorType, errorDescription: detail);

        public static readonly TransactionResult Ok = new();
        public static readonly TransactionResult BlockGasLimitExceeded = new(ErrorType.BlockGasLimitExceeded, errorDescription: "Block gas limit exceeded");
        public static readonly TransactionResult GasLimitBelowIntrinsicGas = new(ErrorType.GasLimitBelowIntrinsicGas, errorDescription: "intrinsic gas too low");
        public static readonly TransactionResult GasLimitBelowFloorGas = new(ErrorType.GasLimitBelowFloorGas, errorDescription: "gas below floor data cost");
        public static readonly TransactionResult InsufficientMaxFeePerGasForSenderBalance = new(ErrorType.InsufficientMaxFeePerGasForSenderBalance, errorDescription: TxErrorMessages.InsufficientFundsForGas);
        public static readonly TransactionResult InsufficientSenderBalance = new(ErrorType.InsufficientSenderBalance, errorDescription: TxErrorMessages.InsufficientFundsForTransfer);
        public static readonly TransactionResult MalformedTransaction = new(ErrorType.MalformedTransaction, errorDescription: "malformed");
        public static readonly TransactionResult MinerPremiumNegative = new(ErrorType.MinerPremiumNegative, errorDescription: "miner premium is negative");
        public static readonly TransactionResult NonceOverflow = new(ErrorType.NonceOverflow, errorDescription: "nonce overflow");
        public static readonly TransactionResult SenderHasDeployedCode = new(ErrorType.SenderHasDeployedCode, errorDescription: "sender has deployed code");
        public static readonly TransactionResult SenderNotSpecified = new(ErrorType.SenderNotSpecified, errorDescription: "sender not specified");
        public static readonly TransactionResult TransactionSizeOverMaxInitCodeSize = new(ErrorType.TransactionSizeOverMaxInitCodeSize, errorDescription: "EIP-3860 - transaction size over max init code size");
        public static readonly TransactionResult TransactionNonceTooHigh = new(ErrorType.TransactionNonceTooHigh, errorDescription: "nonce too high");
        public static readonly TransactionResult TransactionNonceTooLow = new(ErrorType.TransactionNonceTooLow, errorDescription: "nonce too low");

        public enum ErrorType
        {
            None,
            BlockGasLimitExceeded,
            GasLimitBelowIntrinsicGas,
            GasLimitBelowFloorGas,
            InsufficientMaxFeePerGasForSenderBalance,
            InsufficientSenderBalance,
            MalformedTransaction,
            MaxFeePerGasBelowBaseFee,
            MinerPremiumNegative,
            NonceOverflow,
            SenderHasDeployedCode,
            SenderNotSpecified,
            TransactionSizeOverMaxInitCodeSize,
            TransactionNonceTooHigh,
            TransactionNonceTooLow,
        }
    }

    public static class TransactionResultExtensions
    {
        public static TransactionResult WithDetail(this TransactionResult.ErrorType errorType, string detail) =>
            TransactionResult.WithDetail(errorType, detail);

        /// <summary>
        /// Composes the user-facing error string from a <see cref="TransactionResult"/> and an optional
        /// tracer-supplied error. Used by <c>eth_call</c> and <c>proof_call</c> so both report the same
        /// error strings for the same failures.
        /// </summary>
        public static string? GetErrorMessage(this TransactionResult txResult, string? tracerError) => txResult switch
        {
            { TransactionExecuted: true } when txResult.EvmExceptionType is not (EvmExceptionType.None or EvmExceptionType.Revert)
                => txResult.ErrorDescription is { Length: > 0 } d ? d : txResult.EvmExceptionType.GetEvmExceptionDescription(),
            { TransactionExecuted: true } when tracerError is not null => tracerError,
            { TransactionExecuted: false, Error: not TransactionResult.ErrorType.None } => txResult.ErrorDescription,
            _ => null,
        };
    }
}
