// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Tracing;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm.TransactionProcessing
{
    public sealed class TransactionProcessor(
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager)
        : TransactionProcessorBase(specProvider, worldState, virtualMachine, codeInfoRepository, logManager);

    public abstract class TransactionProcessorBase : ITransactionProcessor
    {
        protected EthereumEcdsa Ecdsa { get; }
        protected ILogger Logger { get; }
        protected ISpecProvider SpecProvider { get; }
        protected IWorldState WorldState { get; }
        protected IVirtualMachine VirtualMachine { get; }
        private readonly ICodeInfoRepository _codeInfoRepository;
        private SystemTransactionProcessor? _systemTransactionProcessor;
        private readonly ILogManager _logManager;
        private readonly HashSet<Address> _accessedAddresses = [];

        [Flags]
        protected enum ExecutionOptions
        {
            /// <summary>
            /// Just accumulate the state
            /// </summary>
            None = 0,

            /// <summary>
            /// Commit the state after execution
            /// </summary>
            Commit = 1,

            /// <summary>
            /// Restore state after execution
            /// </summary>
            Restore = 2,

            /// <summary>
            /// Skip potential fail checks
            /// </summary>
            Warmup = 4,

            /// <summary>
            /// Skip potential fail checks and commit state after execution
            /// </summary>
            NoValidation = Commit | Warmup,

            /// <summary>
            /// Commit and later restore state also skip validation, use for CallAndRestore
            /// </summary>
            CommitAndRestore = Commit | Restore | NoValidation
        }

        protected TransactionProcessorBase(
            ISpecProvider? specProvider,
            IWorldState? worldState,
            IVirtualMachine? virtualMachine,
            ICodeInfoRepository? codeInfoRepository,
            ILogManager? logManager)
        {
            ArgumentNullException.ThrowIfNull(logManager, nameof(logManager));
            ArgumentNullException.ThrowIfNull(specProvider, nameof(specProvider));
            ArgumentNullException.ThrowIfNull(worldState, nameof(worldState));
            ArgumentNullException.ThrowIfNull(virtualMachine, nameof(virtualMachine));
            ArgumentNullException.ThrowIfNull(codeInfoRepository, nameof(codeInfoRepository));

            Logger = logManager.GetClassLogger();
            SpecProvider = specProvider;
            WorldState = worldState;
            VirtualMachine = virtualMachine;
            _codeInfoRepository = codeInfoRepository;

            Ecdsa = new EthereumEcdsa(specProvider.ChainId);
            _logManager = logManager;
        }

        public TransactionResult CallAndRestore(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            ExecuteCore(transaction, in blCtx, txTracer, ExecutionOptions.CommitAndRestore);

        public TransactionResult BuildUp(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer)
        {
            // we need to treat the result of previous transaction as the original value of next transaction
            // when we do not commit
            WorldState.TakeSnapshot(true);
            return ExecuteCore(transaction, in blCtx, txTracer, ExecutionOptions.None);
        }

        public TransactionResult Execute(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            ExecuteCore(transaction, in blCtx, txTracer, ExecutionOptions.Commit);

        public TransactionResult Trace(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            ExecuteCore(transaction, in blCtx, txTracer, ExecutionOptions.NoValidation);

        public TransactionResult Warmup(Transaction transaction, in BlockExecutionContext blCtx, ITxTracer txTracer) =>
            ExecuteCore(transaction, in blCtx, txTracer, ExecutionOptions.Warmup);

        private TransactionResult ExecuteCore(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
        {
            if (tx.IsSystem())
            {
                _systemTransactionProcessor ??= new SystemTransactionProcessor(SpecProvider, WorldState, VirtualMachine, _codeInfoRepository, _logManager);
                return _systemTransactionProcessor.Execute(tx, blCtx.Header, tracer, opts);
            }

            return Execute(tx, in blCtx, tracer, opts);
        }

        protected virtual TransactionResult Execute(Transaction tx, in BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
        {
            BlockHeader header = blCtx.Header;
            IReleaseSpec spec = GetSpec(tx, header);

            // restore is CallAndRestore - previous call, we will restore state after the execution
            bool restore = opts.HasFlag(ExecutionOptions.Restore);
            // commit - is for standard execute, we will commit thee state after execution
            // !commit - is for build up during block production, we won't commit state after each transaction to support rollbacks
            // we commit only after all block is constructed
            bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;

            TransactionResult result;
            if (!(result = ValidateStatic(tx, header, spec, opts, out long intrinsicGas))) return result;

            UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, header.BaseFeePerGas);

            UpdateMetrics(opts, effectiveGasPrice);

            bool deleteCallerAccount = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);

            if (!(result = ValidateSender(tx, header, spec, tracer, opts))) return result;
            if (!(result = BuyGas(tx, header, spec, tracer, opts, effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee))) return result;
            if (!(result = IncrementNonce(tx, header, spec, tracer, opts))) return result;

            if (commit) WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullTxTracer.Instance, commitStorageRoots: false);

            StackAccessTracker accessTracker = new();
            int delegationRefunds = ProcessDelegations(tx, spec, accessTracker);

            ExecutionEnvironment env = BuildExecutionEnvironment(tx, in blCtx, spec, effectiveGasPrice, _codeInfoRepository, accessTracker);

            long gasAvailable = tx.GasLimit - intrinsicGas;
            ExecuteEvmCall(tx, header, spec, tracer, opts, delegationRefunds, accessTracker, gasAvailable, env, out TransactionSubstate? substate, out long spentGas, out byte statusCode);
            PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, blobBaseFee, statusCode);

            // Finalize
            if (restore)
            {
                WorldState.Reset();
                if (deleteCallerAccount)
                {
                    WorldState.DeleteAccount(tx.SenderAddress!);
                }
                else
                {
                    if (!opts.HasFlag(ExecutionOptions.NoValidation))
                        WorldState.AddToBalance(tx.SenderAddress!, senderReservedGasPayment, spec);
                    DecrementNonce(tx);

                    WorldState.Commit(spec);
                }
            }
            else if (commit)
            {
                WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance, commitStorageRoots: !spec.IsEip658Enabled);
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
                    byte[] output = (substate?.ShouldRevert ?? false) ? substate.Output.ToArray() : Array.Empty<byte>();
                    tracer.MarkAsFailed(env.ExecutingAccount, spentGas, output, substate?.Error, stateRoot);
                }
                else
                {
                    LogEntry[] logs = substate.Logs.Count != 0 ? substate.Logs.ToArray() : Array.Empty<LogEntry>();
                    tracer.MarkAsSuccess(env.ExecutingAccount, spentGas, substate.Output.ToArray(), logs, stateRoot);
                }
            }

            return TransactionResult.Ok;
        }

        private int ProcessDelegations(Transaction tx, IReleaseSpec spec, in StackAccessTracker accessTracker)
        {
            int refunds = 0;
            if (spec.IsEip7702Enabled && tx.HasAuthorizationList)
            {
                foreach (AuthorizationTuple authTuple in tx.AuthorizationList)
                {
                    authTuple.Authority ??= Ecdsa.RecoverAddress(authTuple);

                    if (!IsValidForExecution(authTuple, accessTracker, out _))
                    {
                        if (Logger.IsDebug) Logger.Debug($"Delegation {authTuple} is invalid");
                    }
                    else
                    {
                        if (!WorldState.AccountExists(authTuple.Authority!))
                        {
                            WorldState.CreateAccount(authTuple.Authority, 0, 1);
                        }
                        else
                        {
                            refunds++;
                            WorldState.IncrementNonce(authTuple.Authority);
                        }

                        _codeInfoRepository.SetDelegation(WorldState, authTuple.CodeAddress, authTuple.Authority, spec);
                    }
                }

            }

            return refunds;

            bool IsValidForExecution(
                AuthorizationTuple authorizationTuple,
                StackAccessTracker accessTracker,
                [NotNullWhen(false)] out string? error)
            {
                UInt256 s = new(authorizationTuple.AuthoritySignature.SAsSpan, isBigEndian: true);
                if (authorizationTuple.Authority is null
                    || s > Secp256K1Curve.HalfN
                    //V minus the offset can only be 1 or 0 since eip-155 does not apply to Setcode signatures
                    || (authorizationTuple.AuthoritySignature.V - Signature.VOffset > 1))
                {
                    error = "Bad signature.";
                    return false;
                }

                if ((authorizationTuple.ChainId != 0
                    && SpecProvider.ChainId != authorizationTuple.ChainId)
                    )
                {
                    error = $"Chain id ({authorizationTuple.ChainId}) does not match.";
                    return false;
                }

                if (authorizationTuple.Nonce == ulong.MaxValue)
                {
                    error = $"Nonce ({authorizationTuple.Nonce}) must be less than 2**64 - 1.";
                    return false;
                }

                if (authorizationTuple.AuthoritySignature.ChainId is not null && authorizationTuple.AuthoritySignature.ChainId != authorizationTuple.ChainId)
                {
                    error = "Bad signature.";
                    return false;
                }

                if (authorizationTuple.Nonce == ulong.MaxValue)
                {
                    error = $"Nonce ({authorizationTuple.Nonce}) must be less than 2**64 - 1.";
                    return false;
                }
                accessTracker.WarmUp(authorizationTuple.Authority);

                if (WorldState.HasCode(authorizationTuple.Authority) && !_codeInfoRepository.TryGetDelegation(WorldState, authorizationTuple.Authority, out _))
                {
                    error = $"Authority ({authorizationTuple.Authority}) has code deployed.";
                    return false;
                }
                UInt256 authNonce = WorldState.GetNonce(authorizationTuple.Authority);
                if (authNonce != authorizationTuple.Nonce)
                {
                    error = $"Skipping tuple in authorization_list because nonce is set to {authorizationTuple.Nonce}, but authority ({authorizationTuple.Authority}) has {authNonce}.";
                    return false;
                }

                error = null;
                return true;
            }
        }

        protected virtual IReleaseSpec GetSpec(Transaction tx, BlockHeader header) => SpecProvider.GetSpec(header);

        private static void UpdateMetrics(ExecutionOptions opts, UInt256 effectiveGasPrice)
        {
            if (opts is ExecutionOptions.Commit or ExecutionOptions.None && (effectiveGasPrice[2] | effectiveGasPrice[3]) == 0)
            {
                float gasPrice = (float)((double)effectiveGasPrice / 1_000_000_000.0);

                Metrics.MinGasPrice = Math.Min(gasPrice, Metrics.MinGasPrice);
                Metrics.MaxGasPrice = Math.Max(gasPrice, Metrics.MaxGasPrice);

                Metrics.BlockMinGasPrice = Math.Min(gasPrice, Metrics.BlockMinGasPrice);
                Metrics.BlockMaxGasPrice = Math.Max(gasPrice, Metrics.BlockMaxGasPrice);

                Metrics.AveGasPrice = (Metrics.AveGasPrice * Metrics.Transactions + gasPrice) / (Metrics.Transactions + 1);
                Metrics.EstMedianGasPrice += Metrics.AveGasPrice * 0.01f * float.Sign(gasPrice - Metrics.EstMedianGasPrice);
                Metrics.Transactions++;

                Metrics.BlockAveGasPrice = (Metrics.BlockAveGasPrice * Metrics.BlockTransactions + gasPrice) / (Metrics.BlockTransactions + 1);
                Metrics.BlockEstMedianGasPrice += Metrics.BlockAveGasPrice * 0.01f * float.Sign(gasPrice - Metrics.BlockEstMedianGasPrice);
                Metrics.BlockTransactions++;
            }
        }

        /// <summary>
        /// Validates the transaction, in a static manner (i.e. without accesing state/storage).
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
            out long intrinsicGas)
        {
            intrinsicGas = IntrinsicGasCalculator.Calculate(tx, spec);

            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

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

            return ValidateGas(tx, header, intrinsicGas, validate);
        }

        protected virtual TransactionResult ValidateGas(Transaction tx, BlockHeader header, long intrinsicGas, bool validate)
        {
            if (tx.GasLimit < intrinsicGas)
            {
                TraceLogInvalidTx(tx, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {tx.GasLimit} < {intrinsicGas}");
                return TransactionResult.GasLimitBelowIntrinsicGas;
            }

            if (validate && tx.GasLimit > header.GasLimit - header.GasUsed)
            {
                TraceLogInvalidTx(tx, $"BLOCK_GAS_LIMIT_EXCEEDED {tx.GasLimit} > {header.GasLimit} - {header.GasUsed}");
                return TransactionResult.BlockGasLimitExceeded;
            }

            return TransactionResult.Ok;
        }

        // TODO Should we remove this already
        protected virtual bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
        {
            bool deleteCallerAccount = false;
            Address? sender = tx.SenderAddress;
            if (sender is null || !WorldState.AccountExists(sender))
            {
                bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;
                bool restore = opts.HasFlag(ExecutionOptions.Restore);
                bool noValidation = opts.HasFlag(ExecutionOptions.NoValidation);

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
                        WorldState.CreateAccount(sender, in UInt256.Zero);
                    }
                }

                if (sender is null)
                {
                    ThrowInvalidDataException($"Failed to recover sender address on tx {tx.Hash} when previously recovered sender account did not exist.");
                }
            }

            return deleteCallerAccount;
        }


        protected virtual TransactionResult ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (validate && WorldState.IsInvalidContractSender(spec, tx.SenderAddress!))
            {
                TraceLogInvalidTx(tx, "SENDER_IS_CONTRACT");
                return TransactionResult.SenderHasDeployedCode;
            }

            return TransactionResult.Ok;
        }

        protected virtual TransactionResult BuyGas(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
                in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
        {
            premiumPerGas = UInt256.Zero;
            senderReservedGasPayment = UInt256.Zero;
            blobBaseFee = UInt256.Zero;
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (validate)
            {
                if (!tx.TryCalculatePremiumPerGas(header.BaseFeePerGas, out premiumPerGas))
                {
                    TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
                    return TransactionResult.MinerPremiumNegative;
                }

                UInt256 senderBalance = WorldState.GetBalance(tx.SenderAddress!);
                if (UInt256.SubtractUnderflow(senderBalance, tx.Value, out UInt256 balanceLeft))
                {
                    TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                    return TransactionResult.InsufficientSenderBalance;
                }

                bool overflows;
                if (spec.IsEip1559Enabled && !tx.IsFree())
                {
                    overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 maxGasFee);
                    if (overflows || balanceLeft < maxGasFee)
                    {
                        TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}, MAX_FEE_PER_GAS: {tx.MaxFeePerGas}");
                        return TransactionResult.InsufficientMaxFeePerGasForSenderBalance;
                    }

                    if (tx.SupportsBlobs)
                    {
                        overflows = UInt256.MultiplyOverflow(BlobGasCalculator.CalculateBlobGas(tx), (UInt256)tx.MaxFeePerBlobGas!, out UInt256 maxBlobGasFee);
                        if (overflows || UInt256.AddOverflow(maxGasFee, maxBlobGasFee, out UInt256 multidimGasFee) || multidimGasFee > balanceLeft)
                        {
                            TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_BLOB_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                            return TransactionResult.InsufficientSenderBalance;
                        }
                    }
                }

                overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out senderReservedGasPayment);
                if (!overflows && tx.SupportsBlobs)
                {
                    overflows = !BlobGasCalculator.TryCalculateBlobBaseFee(header, tx, out blobBaseFee);
                    if (!overflows)
                    {
                        overflows = UInt256.AddOverflow(senderReservedGasPayment, blobBaseFee, out senderReservedGasPayment);
                    }
                }

                if (overflows || senderReservedGasPayment > balanceLeft)
                {
                    TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                    return TransactionResult.InsufficientSenderBalance;
                }
            }

            if (validate) WorldState.SubtractFromBalance(tx.SenderAddress, senderReservedGasPayment, spec);

            return TransactionResult.Ok;
        }

        protected virtual TransactionResult IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            if (tx.Nonce != WorldState.GetNonce(tx.SenderAddress!))
            {
                TraceLogInvalidTx(tx, $"WRONG_TRANSACTION_NONCE: {tx.Nonce} (expected {WorldState.GetNonce(tx.SenderAddress)})");
                return TransactionResult.WrongTransactionNonce;
            }

            WorldState.IncrementNonce(tx.SenderAddress);
            return TransactionResult.Ok;
        }

        protected virtual void DecrementNonce(Transaction tx)
        {
            WorldState.DecrementNonce(tx.SenderAddress!);
        }

        private ExecutionEnvironment BuildExecutionEnvironment(
            Transaction tx,
            in BlockExecutionContext blCtx,
            IReleaseSpec spec,
            in UInt256 effectiveGasPrice,
            ICodeInfoRepository codeInfoRepository,
            in StackAccessTracker accessTracker)
        {
            Address recipient = tx.GetRecipient(tx.IsContractCreation ? WorldState.GetNonce(tx.SenderAddress!) : 0);
            if (recipient is null) ThrowInvalidDataException("Recipient has not been resolved properly before tx execution");

            TxExecutionContext executionContext = new(in blCtx, tx.SenderAddress, effectiveGasPrice, tx.BlobVersionedHashes, codeInfoRepository);
            Address? delegationAddress = null;
            CodeInfo codeInfo = tx.IsContractCreation
                ? new(tx.Data ?? Memory<byte>.Empty)
                : codeInfoRepository.GetCachedCodeInfo(WorldState, recipient, spec, out delegationAddress);
            codeInfo.AnalyseInBackgroundIfRequired();

            if (spec.UseHotAndColdStorage)
            {
                if (spec.UseTxAccessLists)
                    accessTracker.WarmUp(tx.AccessList); // eip-2930

                accessTracker.WarmUp(recipient);
                accessTracker.WarmUp(tx.SenderAddress!);

                if (spec.AddCoinbaseToTxAccessList)
                    accessTracker.WarmUp(blCtx.Header.GasBeneficiary!);

                //We assume eip-7702 must be active if it is a delegation
                if (delegationAddress is not null)
                    accessTracker.WarmUp(delegationAddress);
            }

            ReadOnlyMemory<byte> inputData = tx.IsMessageCall ? tx.Data ?? default : default;

            return new ExecutionEnvironment
            (
                txExecutionContext: in executionContext,
                value: tx.Value,
                transferValue: tx.Value,
                caller: tx.SenderAddress,
                codeSource: recipient,
                executingAccount: recipient,
                inputData: inputData,
                codeInfo: codeInfo
            );
        }

        protected virtual bool ShouldValidate(ExecutionOptions opts) => !opts.HasFlag(ExecutionOptions.NoValidation);

        protected virtual void ExecuteEvmCall(
            Transaction tx,
            BlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            ExecutionOptions opts,
            int delegationRefunds,
            in StackAccessTracker accessedItems,
            in long gasAvailable,
            in ExecutionEnvironment env,
            out TransactionSubstate? substate,
            out long spentGas,
            out byte statusCode)
        {
            bool validate = ShouldValidate(opts);

            substate = null;
            spentGas = tx.GasLimit;
            statusCode = StatusCode.Failure;

            long unspentGas = gasAvailable;

            Snapshot snapshot = WorldState.TakeSnapshot();

            PayValue(tx, spec, opts);

            try
            {
                if (tx.IsContractCreation)
                {
                    // if transaction is a contract creation then recipient address is the contract deployment address
                    if (!PrepareAccountForContractDeployment(env.ExecutingAccount, _codeInfoRepository, spec))
                    {
                        goto Fail;
                    }
                }

                using (EvmState state = new EvmState(
                    unspentGas,
                    env,
                    tx.IsContractCreation ? ExecutionType.CREATE : ExecutionType.TRANSACTION,
                    snapshot,
                    accessedItems))
                {

                    substate = !tracer.IsTracingActions
                        ? VirtualMachine.Run<NotTracing>(state, WorldState, tracer)
                        : VirtualMachine.Run<IsTracing>(state, WorldState, tracer);

                    unspentGas = state.GasAvailable;

                    if (tracer.IsTracingAccess)
                    {
                        tracer.ReportAccess(state.AccessTracker.AccessedAddresses, state.AccessTracker.AccessedStorageCells);
                    }
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (Logger.IsTrace) Logger.Trace("Restoring state from before transaction");
                    WorldState.Restore(snapshot);
                }
                else
                {
                    // tks: there is similar code fo contract creation from init and from CREATE
                    // this may lead to inconsistencies (however it is tested extensively in blockchain tests)
                    if (tx.IsContractCreation)
                    {
                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(substate.Output.Length, spec);
                        if (unspentGas < codeDepositGasCost && spec.ChargeForTopLevelCreate)
                        {
                            goto Fail;
                        }

                        if (CodeDepositHandler.CodeIsInvalid(spec, substate.Output))
                        {
                            goto Fail;
                        }

                        if (unspentGas >= codeDepositGasCost)
                        {
                            var code = substate.Output.ToArray();
                            _codeInfoRepository.InsertCode(WorldState, code, env.ExecutingAccount, spec);

                            unspentGas -= codeDepositGasCost;
                        }
                    }

                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"Destroying account {toBeDestroyed}");

                        WorldState.ClearStorage(toBeDestroyed);
                        WorldState.DeleteAccount(toBeDestroyed);

                        if (tracer.IsTracingRefunds)
                            tracer.ReportRefund(RefundOf.Destroy(spec.IsEip3529Enabled));
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(tx, header, spec, opts, substate, unspentGas, env.TxExecutionContext.GasPrice, delegationRefunds);
                goto Complete;
            }
            catch (Exception ex) when (ex is EvmException or OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (Logger.IsTrace) Logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}:{ex.Message}");
            }
        Fail:
            if (Logger.IsTrace) Logger.Trace("Restoring state from before transaction");
            WorldState.Restore(snapshot);

        Complete:
            if (!opts.HasFlag(ExecutionOptions.NoValidation))
                header.GasUsed += spentGas;
        }

        protected virtual void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts)
        {
            WorldState.SubtractFromBalance(tx.SenderAddress!, tx.Value, spec);
        }

        protected virtual void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in UInt256 blobBaseFee, in byte statusCode)
        {
            bool gasBeneficiaryNotDestroyed = substate?.DestroyList.Contains(header.GasBeneficiary) != true;
            if (statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed)
            {
                UInt256 fees = (UInt256)spentGas * premiumPerGas;
                UInt256 eip1559Fees = !tx.IsFree() ? (UInt256)spentGas * header.BaseFeePerGas : UInt256.Zero;
                UInt256 collectedFees = spec.IsEip1559Enabled ? eip1559Fees : UInt256.Zero;

                if (tx.SupportsBlobs && spec.IsEip4844FeeCollectorEnabled)
                {
                    collectedFees += blobBaseFee;
                }

                WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec);

                if (spec.FeeCollector is not null && !collectedFees.IsZero)
                    WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, collectedFees, spec);

                if (tracer.IsTracingFees)
                    tracer.ReportFees(fees, eip1559Fees + blobBaseFee);
            }
        }

        protected bool PrepareAccountForContractDeployment(Address contractAddress, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec)
        {
            if (WorldState.AccountExists(contractAddress) && contractAddress.IsNonZeroAccount(spec, codeInfoRepository, WorldState))
            {
                if (Logger.IsTrace) Logger.Trace($"Contract collision at {contractAddress}");

                return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (Logger.IsTrace) Logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }

        protected virtual long Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
            in TransactionSubstate substate, in long unspentGas, in UInt256 gasPrice, int codeInsertRefunds)
        {
            long spentGas = tx.GasLimit;
            var codeInsertRefund = (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds;

            if (!substate.IsError)
            {
                spentGas -= unspentGas;

                long totalToRefund = codeInsertRefund;
                if (!substate.ShouldRevert)
                    totalToRefund += substate.Refund + substate.DestroyList.Count * RefundOf.Destroy(spec.IsEip3529Enabled);
                long actualRefund = RefundHelper.CalculateClaimableRefund(spentGas, totalToRefund, spec);

                if (Logger.IsTrace)
                    Logger.Trace("Refunding unused gas of " + unspentGas + " and refund of " + actualRefund);
                // If noValidation we didn't charge for gas, so do not refund
                if (!opts.HasFlag(ExecutionOptions.NoValidation))
                    WorldState.AddToBalance(tx.SenderAddress!, (ulong)(unspentGas + actualRefund) * gasPrice, spec);
                spentGas -= actualRefund;
            }
            else if (codeInsertRefund > 0)
            {
                long refund = RefundHelper.CalculateClaimableRefund(spentGas, codeInsertRefund, spec);

                if (Logger.IsTrace)
                    Logger.Trace("Refunding delegations only: " + refund);
                // If noValidation we didn't charge for gas, so do not refund
                if (!opts.HasFlag(ExecutionOptions.NoValidation))
                    WorldState.AddToBalance(tx.SenderAddress!, (ulong)refund * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowInvalidDataException(string message) => throw new InvalidDataException(message);
    }

    public readonly struct TransactionResult(string? error) : IEquatable<TransactionResult>
    {
        [MemberNotNullWhen(true, nameof(Fail))]
        [MemberNotNullWhen(false, nameof(Success))]
        public string? Error { get; } = error;
        public bool Fail => Error is not null;
        public bool Success => Error is null;

        public static implicit operator TransactionResult(string? error) => new(error);
        public static implicit operator bool(TransactionResult result) => result.Success;
        public bool Equals(TransactionResult other) => (Success && other.Success) || (Error == other.Error);
        public static bool operator ==(TransactionResult obj1, TransactionResult obj2) => obj1.Equals(obj2);
        public static bool operator !=(TransactionResult obj1, TransactionResult obj2) => !obj1.Equals(obj2);
        public override bool Equals(object? obj) => obj is TransactionResult result && Equals(result);
        public override int GetHashCode() => Success ? 1 : Error.GetHashCode();

        public override string ToString() => Error is not null ? $"Fail : {Error}" : "Success";

        public static readonly TransactionResult Ok = new();

        public static readonly TransactionResult BlockGasLimitExceeded = "Block gas limit exceeded";
        public static readonly TransactionResult GasLimitBelowIntrinsicGas = "gas limit below intrinsic gas";
        public static readonly TransactionResult InsufficientMaxFeePerGasForSenderBalance = "insufficient MaxFeePerGas for sender balance";
        public static readonly TransactionResult InsufficientSenderBalance = "insufficient sender balance";
        public static readonly TransactionResult MalformedTransaction = "malformed";
        public static readonly TransactionResult MinerPremiumNegative = "miner premium is negative";
        public static readonly TransactionResult NonceOverflow = "nonce overflow";
        public static readonly TransactionResult SenderHasDeployedCode = "sender has deployed code";
        public static readonly TransactionResult SenderNotSpecified = "sender not specified";
        public static readonly TransactionResult TransactionSizeOverMaxInitCodeSize = "EIP-3860 - transaction size over max init code size";
        public static readonly TransactionResult WrongTransactionNonce = "wrong transaction nonce";
    }
}
