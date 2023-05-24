// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Evm.TransactionProcessing
{
    public class TransactionProcessor : ITransactionProcessor
    {
        private readonly EthereumEcdsa _ecdsa;
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IWorldState _worldState;
        private readonly IVirtualMachine _virtualMachine;

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
            NoValidation = Commit | 4,

            /// <summary>
            /// Commit and later restore state also skip validation, use for CallAndRestore
            /// </summary>
            CommitAndRestore = Commit | Restore | NoValidation
        }

        public TransactionProcessor(
            ISpecProvider? specProvider,
            IStateProvider? stateProvider,
            IStorageProvider? storageProvider,
            IVirtualMachine? virtualMachine,
            ILogManager? logManager)
            : this(specProvider, new WorldState(stateProvider, storageProvider), virtualMachine, logManager) { }

        public TransactionProcessor(
            ISpecProvider? specProvider,
            IWorldState? worldState,
            IVirtualMachine? virtualMachine,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
            _stateProvider = worldState.StateProvider;
            _storageProvider = worldState.StorageProvider;
            _virtualMachine = virtualMachine ?? throw new ArgumentNullException(nameof(virtualMachine));
            _ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
        }

        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block, txTracer, ExecutionOptions.CommitAndRestore);
        }

        public void BuildUp(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            // we need to treat the result of previous transaction as the original value of next transaction
            // when we do not commit
            _worldState.TakeSnapshot(true);
            Execute(transaction, block, txTracer, ExecutionOptions.None);
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block, txTracer, ExecutionOptions.Commit);
        }

        public void Trace(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block, txTracer, ExecutionOptions.NoValidation);
        }

        private void QuickFail(Transaction tx, BlockHeader block, IReleaseSpec spec, ITxTracer txTracer, string? reason)
        {
            block.GasUsed += tx.GasLimit;

            Address recipient = tx.To ?? ContractAddress.From(
                tx.SenderAddress ?? Address.Zero,
                _stateProvider.GetNonce(tx.SenderAddress ?? Address.Zero));

            if (txTracer.IsTracingReceipt)
            {
                Keccak? stateRoot = null;
                if (!spec.IsEip658Enabled)
                {
                    _stateProvider.RecalculateStateRoot();
                    stateRoot = _stateProvider.StateRoot;
                }

                txTracer.MarkAsFailed(recipient, tx.GasLimit, Array.Empty<byte>(), reason ?? "invalid", stateRoot);
            }
        }


        /// <summary>
        /// Validates the transaction, in a static manner (i.e. without accesing state/storage).
        /// It basically ensures the transaction is well formed (i.e. no null values where not allowed, no overflows, etc).
        /// As a part of validating the transaction the premium per gas will be calculated, to save computation this
        /// is returned in an out parameter.
        /// </summary>
        /// <param name="tx">The transaction to validate</param>
        /// <param name="blk">The block containing the transaction. Only BaseFee is being used from the block atm.</param>
        /// <param name="spec">The release spec with which the transaction will be executed</param>
        /// <param name="tracer">The transaction tracer</param>
        /// <param name="opts">Options (Flags) to use for execution</param>
        /// <param name="premium">Computed premium per gas</param>
        /// <returns></returns>
        protected virtual bool ValidateStatic(Transaction tx, BlockHeader blk, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts, out long intrinsicGas)
        {
            intrinsicGas = IntrinsicGasCalculator.Calculate(tx, spec);

            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (tx.SenderAddress is null)
            {
                TraceLogInvalidTx(tx, "SENDER_NOT_SPECIFIED");
                QuickFail(tx, blk, spec, tracer, "sender not specified");
                return false;
            }

            if (validate && tx.Nonce >= ulong.MaxValue - 1)
            {
                // we are here if nonce is at least (ulong.MaxValue - 1). If tx is contract creation,
                // it is max possible value. Otherwise, (ulong.MaxValue - 1) is allowed, but ulong.MaxValue not.
                if (tx.IsContractCreation || tx.Nonce == ulong.MaxValue)
                {
                    TraceLogInvalidTx(tx, "NONCE_OVERFLOW");
                    QuickFail(tx, blk, spec, tracer, "nonce overflow");
                    return false;
                }
            }

            if (tx.IsAboveInitCode(spec))
            {
                TraceLogInvalidTx(tx, $"CREATE_TRANSACTION_SIZE_EXCEEDS_MAX_INIT_CODE_SIZE {tx.DataLength} > {spec.MaxInitCodeSize}");
                QuickFail(tx, blk, spec, tracer, "EIP-3860 - transaction size over max init code size");
                return false;
            }

            if (!tx.IsSystem())
            {
                if (tx.GasLimit < intrinsicGas)
                {
                    TraceLogInvalidTx(tx, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {tx.GasLimit} < {intrinsicGas}");
                    QuickFail(tx, blk, spec, tracer, "gas limit below intrinsic gas");
                    return false;
                }

                if (validate && tx.GasLimit > blk.GasLimit - blk.GasUsed)
                {
                    TraceLogInvalidTx(tx, $"BLOCK_GAS_LIMIT_EXCEEDED {tx.GasLimit} > {blk.GasLimit} - {blk.GasUsed}");
                    QuickFail(tx, blk, spec, tracer, "block gas limit exceeded");
                    return false;
                }
            }

            return true;
        }

        // TODO Should we remove this already
        private bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
        {
            bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;
            bool restore = opts.HasFlag(ExecutionOptions.Restore);
            bool noValidation = opts.HasFlag(ExecutionOptions.NoValidation);

            bool deleteCallerAccount = false;

            if (!_stateProvider.AccountExists(tx.SenderAddress))
            {
                Address prevSender = tx.SenderAddress;
                // hacky fix for the potential recovery issue
                if (tx.Signature is not null)
                    tx.SenderAddress = _ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

                if (prevSender != tx.SenderAddress)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"TX recovery issue fixed - tx was coming with sender {prevSender} and the now it recovers to {tx.SenderAddress}");
                }
                else
                {
                    TraceLogInvalidTx(tx, $"SENDER_ACCOUNT_DOES_NOT_EXIST {tx.SenderAddress}");
                    if (!commit || noValidation || effectiveGasPrice == UInt256.Zero)
                    {
                        deleteCallerAccount = !commit || restore;
                        _stateProvider.CreateAccount(tx.SenderAddress, UInt256.Zero);
                    }
                }

                if (tx.SenderAddress is null)
                {
                    throw new InvalidDataException($"Failed to recover sender address on tx {tx.Hash} when previously recovered sender account did not exist.");
                }
            }

            return deleteCallerAccount;
        }

        protected virtual bool ValidateSender(Transaction tx, BlockHeader blk, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (validate && _stateProvider.IsInvalidContractSender(spec, tx.SenderAddress))
            {
                // TraceLogInvalidTx(tx, "SENDER_IS_CONTRACT");
                // QuickFail(tx, blk, tracer, eip658NotEnabled, "sender has deployed code");
                return false;
            }

            return true;
        }

        private bool BuyGas(Transaction tx, BlockHeader blk, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
                in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment)
        {
            premiumPerGas = UInt256.Zero;
            senderReservedGasPayment = UInt256.Zero;

            if (opts.HasFlag(ExecutionOptions.NoValidation) || tx.IsSystem())
            {
                _stateProvider.SubtractFromBalance(tx.SenderAddress, UInt256.Zero, spec);
                return true;
            }

            if (!tx.TryCalculatePremiumPerGas(blk.BaseFeePerGas, out premiumPerGas))
            {
                TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
                QuickFail(tx, blk, spec, tracer, "miner premium is negative");
                return false;
            }

            UInt256 senderBalance = _stateProvider.GetBalance(tx.SenderAddress);
            if (UInt256.SubtractUnderflow(senderBalance, tx.Value, out UInt256 balanceLeft))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                QuickFail(tx, blk, spec, tracer, "insufficient sender balance");
                return false;
            }

            bool overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 maxGasFee);
            if (spec.IsEip1559Enabled && !tx.IsFree() && (overflows || balanceLeft < maxGasFee))
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}, MAX_FEE_PER_GAS: {tx.MaxFeePerGas}");
                QuickFail(tx, blk, spec, tracer, "insufficient MaxFeePerGas for sender balance");
                return false;
            }

            overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out senderReservedGasPayment);
            if (overflows || senderReservedGasPayment > balanceLeft)
            {
                TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                QuickFail(tx, blk, spec, tracer, "insufficient sender balance");
                return false;
            }

            _stateProvider.SubtractFromBalance(tx.SenderAddress, senderReservedGasPayment, spec);
            return true;
        }

        protected virtual bool IncrementNonce(Transaction tx, BlockHeader blk, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            if (tx.Nonce != _stateProvider.GetNonce(tx.SenderAddress))
            {
                TraceLogInvalidTx(tx, $"WRONG_TRANSACTION_NONCE: {tx.Nonce} (expected {_stateProvider.GetNonce(tx.SenderAddress)})");
                QuickFail(tx, blk, spec, tracer, "wrong transaction nonce");
                return false;
            }

            _stateProvider.IncrementNonce(tx.SenderAddress);
            return true;
        }

        protected virtual ExecutionEnvironment GetEVMExecutionEnvironment(Transaction tx, BlockHeader blk, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {

        }

        protected virtual bool ExecuteEVMCall(
            Transaction tx, BlockHeader blk, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
            in long gasAvailable, in UInt256 effectiveGasPrice,
            out TransactionSubstate? substate, out long spentGas, out byte statusCode)
        {
            substate = null;
            spentGas = tx.GasLimit;
            statusCode = StatusCode.Failure;

            long unspentGas = gasAvailable;

            Snapshot snapshot = _worldState.TakeSnapshot();
            _stateProvider.SubtractFromBalance(tx.SenderAddress, tx.Value, spec);

            try
            {
                Address? recipient = tx.GetRecipient(tx.IsContractCreation ? _stateProvider.GetNonce(tx.SenderAddress) : 0);
                if (tx.IsContractCreation)
                {
                    // if transaction is a contract creation then recipient address is the contract deployment address
                    PrepareAccountForContractDeployment(recipient, spec);
                }

                if (recipient is null)
                {
                    // this transaction is not a contract creation so it should have the recipient known and not null
                    throw new InvalidDataException("Recipient has not been resolved properly before tx execution");
                }

                TxExecutionContext executionContext =
                    new(blk, tx.SenderAddress, effectiveGasPrice, tx.BlobVersionedHashes);

                CodeInfo codeInfo = tx.IsContractCreation ? new(tx.Data)
                                        : _virtualMachine.GetCachedCodeInfo(_worldState, recipient, spec);

                byte[] inputData = tx.IsMessageCall ? tx.Data ?? Array.Empty<byte>() : Array.Empty<byte>();


                ExecutionEnvironment env = new
                (
                    txExecutionContext: executionContext,
                    value: tx.Value,
                    transferValue: tx.Value,
                    caller: tx.SenderAddress,
                    codeSource: recipient,
                    executingAccount: recipient,
                    inputData: inputData,
                    codeInfo: codeInfo
                );

                ExecutionType executionType =
                    tx.IsContractCreation ? ExecutionType.Create : ExecutionType.Transaction;

                EvmState state = new
                (
                    gasAvailable: unspentGas,
                    env: env,
                    executionType: executionType,
                    isTopLevel: true,
                    snapshot: snapshot,
                    isContinuation: false
                );

                using (state)
                {
                    if (spec.UseTxAccessLists)
                    {
                        state.WarmUp(tx.AccessList); // eip-2930
                    }

                    if (spec.UseHotAndColdStorage)
                    {
                        state.WarmUp(tx.SenderAddress); // eip-2929
                        state.WarmUp(recipient); // eip-2929
                    }

                    if (spec.AddCoinbaseToTxAccessList)
                    {
                        state.WarmUp(blk.GasBeneficiary);
                    }

                    substate = _virtualMachine.Run(state, _worldState, tracer);
                    unspentGas = state.GasAvailable;

                    if (tracer.IsTracingAccess)
                    {
                        tracer.ReportAccess(state.AccessedAddresses, state.AccessedStorageCells);
                    }
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (_logger.IsTrace) _logger.Trace("Restoring state from before transaction");
                    _worldState.Restore(snapshot);
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
                            throw new OutOfGasException();
                        }

                        if (CodeDepositHandler.CodeIsInvalid(spec, substate.Output))
                        {
                            throw new InvalidCodeException();
                        }

                        if (unspentGas >= codeDepositGasCost)
                        {
                            Keccak codeHash = _stateProvider.UpdateCode(substate.Output);
                            _stateProvider.UpdateCodeHash(recipient, codeHash, spec);
                            unspentGas -= codeDepositGasCost;
                        }
                    }

                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        if (_logger.IsTrace)
                            _logger.Trace($"Destroying account {toBeDestroyed}");

                        _storageProvider.ClearStorage(toBeDestroyed);
                        _stateProvider.DeleteAccount(toBeDestroyed);

                        if (tracer.IsTracingRefunds)
                            tracer.ReportRefund(RefundOf.Destroy(spec.IsEip3529Enabled));
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(tx.GasLimit, unspentGas, substate, tx.SenderAddress, effectiveGasPrice, spec);
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (_logger.IsTrace)
                    _logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}");

                _worldState.Restore(snapshot);
            }

            return true;
        }

        protected virtual void Execute(Transaction tx, BlockHeader blk, ITxTracer tracer, ExecutionOptions opts)
        {
            IReleaseSpec spec = _specProvider.GetSpec(blk);
            if (tx.IsSystem())
                spec = new SystemTransactionReleaseSpec(spec);

            // commit - is for standard execute, we will commit thee state after execution
            bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;

            if (!ValidateStatic(tx, blk, spec, tracer, opts, out long intrinsicGas))
                return;

            UInt256 effectiveGasPrice =
                tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, blk.BaseFeePerGas);

            bool deleteCallerAccount = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);

            if (!ValidateSender(tx, blk, spec, tracer, opts))
                return;

            if (!IncrementNonce(tx, blk, spec, tracer, opts))
                return;

            if (!BuyGas(tx, blk, spec, tracer, opts, effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment))
                return;

            if (commit)
                _stateProvider.Commit(spec, tracer.IsTracingState ? tracer : NullTxTracer.Instance);
        }

        private void ExecuteOld(Transaction transaction, BlockHeader block, ITxTracer txTracer, ExecutionOptions executionOptions)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block);
            bool eip658NotEnabled = !spec.IsEip658Enabled;

            // restore is CallAndRestore - previous call, we will restore state after the execution
            bool restore = (executionOptions & ExecutionOptions.Restore) == ExecutionOptions.Restore;
            bool noValidation = (executionOptions & ExecutionOptions.NoValidation) == ExecutionOptions.NoValidation;
            // commit - is for standard execute, we will commit thee state after execution
            bool commit = (executionOptions & ExecutionOptions.Commit) == ExecutionOptions.Commit || eip658NotEnabled;
            //!commit - is for build up during block production, we won't commit state after each transaction to support rollbacks
            //we commit only after all block is constructed
            bool notSystemTransaction = !transaction.IsSystem();
            bool deleteCallerAccount = false;

            if (!notSystemTransaction)
            {
                spec = new SystemTransactionReleaseSpec(spec);
            }

            UInt256 value = transaction.Value;

            if (!transaction.TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 premiumPerGas) && !noValidation)
            {
                TraceLogInvalidTx(transaction, "MINER_PREMIUM_IS_NEGATIVE");
                QuickFail(transaction, block, spec, txTracer, "miner premium is negative");
                return;
            }

            UInt256 effectiveGasPrice =
                transaction.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, block.BaseFeePerGas);

            long gasLimit = transaction.GasLimit;
            byte[]? machineCode = transaction.IsContractCreation ? transaction.Data : null;
            byte[] data = transaction.IsMessageCall ? transaction.Data : Array.Empty<byte>();

            Address? caller = transaction.SenderAddress;
            if (_logger.IsTrace) _logger.Trace($"Executing tx {transaction.Hash}");

            if (caller is null)
            {
                TraceLogInvalidTx(transaction, "SENDER_NOT_SPECIFIED");
                QuickFail(transaction, block, spec, txTracer, "sender not specified");
                return;
            }

            if (!noValidation && _stateProvider.IsInvalidContractSender(spec, caller))
            {
                TraceLogInvalidTx(transaction, "SENDER_IS_CONTRACT");
                QuickFail(transaction, block, spec, txTracer, "sender has deployed code");
                return;
            }

            if (!noValidation && transaction.Nonce >= ulong.MaxValue - 1)
            {
                // we are here if nonce is at least (ulong.MaxValue - 1). If tx is contract creation,
                // it is max possible value. Otherwise, (ulong.MaxValue - 1) is allowed, but ulong.MaxValue not.
                if (transaction.IsContractCreation || transaction.Nonce == ulong.MaxValue)
                {
                    TraceLogInvalidTx(transaction, "NONCE_OVERFLOW");
                    QuickFail(transaction, block, spec, txTracer, "nonce overflow");
                    return;
                }
            }

            if (transaction.IsAboveInitCode(spec))
            {
                TraceLogInvalidTx(transaction, $"CREATE_TRANSACTION_SIZE_EXCEEDS_MAX_INIT_CODE_SIZE {transaction.DataLength} > {spec.MaxInitCodeSize}");
                QuickFail(transaction, block, spec, txTracer, "EIP-3860 - transaction size over max init code size");
                return;
            }

            long intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsTrace) _logger.Trace($"Intrinsic gas calculated for {transaction.Hash}: " + intrinsicGas);

            if (notSystemTransaction)
            {
                if (gasLimit < intrinsicGas)
                {
                    TraceLogInvalidTx(transaction, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                    QuickFail(transaction, block, spec, txTracer, "gas limit below intrinsic gas");
                    return;
                }

                if (!noValidation && gasLimit > block.GasLimit - block.GasUsed)
                {
                    TraceLogInvalidTx(transaction,
                        $"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GasLimit} - {block.GasUsed}");
                    QuickFail(transaction, block, spec, txTracer, "block gas limit exceeded");
                    return;
                }
            }

            if (!_stateProvider.AccountExists(caller))
            {
                // hacky fix for the potential recovery issue
                if (transaction.Signature is not null)
                {
                    transaction.SenderAddress = _ecdsa.RecoverAddress(transaction, !spec.ValidateChainId);
                }

                if (caller != transaction.SenderAddress)
                {
                    if (_logger.IsWarn)
                        _logger.Warn(
                            $"TX recovery issue fixed - tx was coming with sender {caller} and the now it recovers to {transaction.SenderAddress}");
                    caller = transaction.SenderAddress;
                }
                else
                {
                    TraceLogInvalidTx(transaction, $"SENDER_ACCOUNT_DOES_NOT_EXIST {caller}");
                    if (!commit || noValidation || effectiveGasPrice == UInt256.Zero)
                    {
                        deleteCallerAccount = !commit || restore;
                        _stateProvider.CreateAccount(caller, UInt256.Zero);
                    }
                }

                if (caller is null)
                {
                    throw new InvalidDataException(
                        $"Failed to recover sender address on tx {transaction.Hash} when previously recovered sender account did not exist.");
                }
            }



            if (notSystemTransaction)
            {
                if (!noValidation && !ValidateFees((ulong)intrinsicGas, in effectiveGasPrice, out UInt256 reservedGasPayment, transaction, block, txTracer, spec))
                {
                    return;
                }

                if (!ValidateNonce(transaction, block, spec, txTracer, executionOptions))
                {
                    return;
                }

                _stateProvider.IncrementNonce(caller);
            }

            UInt256 senderReservedGasPayment = noValidation ? UInt256.Zero : (UInt256)gasLimit * effectiveGasPrice;
            _stateProvider.SubtractFromBalance(caller, senderReservedGasPayment, spec);
            if (commit)
            {
                _stateProvider.Commit(spec, txTracer.IsTracingState ? txTracer : NullTxTracer.Instance);
            }

            long unspentGas = gasLimit - intrinsicGas;
            long spentGas = gasLimit;

            Snapshot snapshot = _worldState.TakeSnapshot();
            _stateProvider.SubtractFromBalance(caller, value, spec);
            byte statusCode = StatusCode.Failure;
            TransactionSubstate substate = null;

            Address? recipientOrNull = null;
            try
            {
                Address? recipient =
                    transaction.GetRecipient(transaction.IsContractCreation ? _stateProvider.GetNonce(caller) : 0);
                if (transaction.IsContractCreation)
                {
                    // if transaction is a contract creation then recipient address is the contract deployment address
                    Address contractAddress = recipient;
                    PrepareAccountForContractDeployment(contractAddress!, spec);
                }

                if (recipient is null)
                {
                    // this transaction is not a contract creation so it should have the recipient known and not null
                    throw new InvalidDataException("Recipient has not been resolved properly before tx execution");
                }

                recipientOrNull = recipient;

                ExecutionEnvironment env = new
                (
                    txExecutionContext: new TxExecutionContext(block, caller, effectiveGasPrice, transaction.BlobVersionedHashes),
                    value: value,
                    transferValue: value,
                    caller: caller,
                    codeSource: recipient,
                    executingAccount: recipient,
                    inputData: data ?? Array.Empty<byte>(),
                    codeInfo: machineCode is null
                        ? _virtualMachine.GetCachedCodeInfo(_worldState, recipient, spec)
                        : new CodeInfo(machineCode)
                );
                ExecutionType executionType =
                    transaction.IsContractCreation ? ExecutionType.Create : ExecutionType.Transaction;
                using (EvmState state =
                    new(unspentGas, env, executionType, true, snapshot, false))
                {
                    if (spec.UseTxAccessLists)
                    {
                        state.WarmUp(transaction.AccessList); // eip-2930
                    }

                    if (spec.UseHotAndColdStorage)
                    {
                        state.WarmUp(caller); // eip-2929
                        state.WarmUp(recipient); // eip-2929
                    }

                    if (spec.AddCoinbaseToTxAccessList)
                    {
                        state.WarmUp(block.GasBeneficiary);
                    }

                    substate = _virtualMachine.Run(state, _worldState, txTracer);
                    unspentGas = state.GasAvailable;

                    if (txTracer.IsTracingAccess)
                    {
                        txTracer.ReportAccess(state.AccessedAddresses, state.AccessedStorageCells);
                    }
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (_logger.IsTrace) _logger.Trace("Restoring state from before transaction");
                    _worldState.Restore(snapshot);
                }
                else
                {
                    // tks: there is similar code fo contract creation from init and from CREATE
                    // this may lead to inconsistencies (however it is tested extensively in blockchain tests)
                    if (transaction.IsContractCreation)
                    {
                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(substate.Output.Length, spec);
                        if (unspentGas < codeDepositGasCost && spec.ChargeForTopLevelCreate)
                        {
                            throw new OutOfGasException();
                        }

                        if (CodeDepositHandler.CodeIsInvalid(spec, substate.Output))
                        {
                            throw new InvalidCodeException();
                        }

                        if (unspentGas >= codeDepositGasCost)
                        {
                            Keccak codeHash = _stateProvider.UpdateCode(substate.Output);
                            _stateProvider.UpdateCodeHash(recipient, codeHash, spec);
                            unspentGas -= codeDepositGasCost;
                        }
                    }

                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Destroying account {toBeDestroyed}");
                        _storageProvider.ClearStorage(toBeDestroyed);
                        _stateProvider.DeleteAccount(toBeDestroyed);
                        if (txTracer.IsTracingRefunds) txTracer.ReportRefund(RefundOf.Destroy(spec.IsEip3529Enabled));
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(gasLimit, unspentGas, substate, caller, effectiveGasPrice, spec);
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (_logger.IsTrace) _logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}");
                _worldState.Restore(snapshot);
            }

            if (_logger.IsTrace) _logger.Trace("Gas spent: " + spentGas);

            Address gasBeneficiary = block.GasBeneficiary;
            bool gasBeneficiaryNotDestroyed = substate?.DestroyList.Contains(gasBeneficiary) != true;
            if (statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed)
            {
                if (notSystemTransaction)
                {
                    CalculateFees((ulong)spentGas, premiumPerGas, transaction, block, txTracer, gasBeneficiary, spec);
                }
            }

            if (restore)
            {
                _storageProvider.Reset();
                _stateProvider.Reset();
                if (deleteCallerAccount)
                {
                    _stateProvider.DeleteAccount(caller);
                }
                else
                {
                    _stateProvider.AddToBalance(caller, senderReservedGasPayment, spec);
                    if (notSystemTransaction)
                    {
                        _stateProvider.DecrementNonce(caller);
                    }

                    _stateProvider.Commit(spec);
                }
            }
            else if (commit)
            {
                _storageProvider.Commit(txTracer.IsTracingState ? txTracer : NullStorageTracer.Instance);
                _stateProvider.Commit(spec, txTracer.IsTracingState ? txTracer : NullStateTracer.Instance);
            }

            if (!noValidation && notSystemTransaction)
            {
                block.GasUsed += spentGas;
            }

            if (txTracer.IsTracingReceipt)
            {
                Keccak stateRoot = null;
                if (eip658NotEnabled)
                {
                    _stateProvider.RecalculateStateRoot();
                    stateRoot = _stateProvider.StateRoot;
                }

                if (statusCode == StatusCode.Failure)
                {
                    txTracer.MarkAsFailed(recipientOrNull, spentGas,
                        (substate?.ShouldRevert ?? false) ? substate.Output.ToArray() : Array.Empty<byte>(),
                        substate?.Error, stateRoot);
                }
                else
                {
                    txTracer.MarkAsSuccess(recipientOrNull, spentGas, substate.Output.ToArray(),
                        substate.Logs.Any() ? substate.Logs.ToArray() : Array.Empty<LogEntry>(), stateRoot);
                }
            }
        }

        private void CalculateFees(ulong spentGas, UInt256 premiumPerGas, Transaction transaction, BlockHeader block,
            ITxTracer txTracer, Address gasBeneficiary, IReleaseSpec spec)
        {
            UInt256 fees = spentGas * premiumPerGas;
            _stateProvider.AddToBalanceAndCreateIfNotExists(gasBeneficiary, fees, spec);

            UInt256 burntFees = !transaction.IsFree() ? spentGas * block.BaseFeePerGas : 0;

            if (spec.IsEip1559Enabled && spec.Eip1559FeeCollector is not null)
            {
                if (!burntFees.IsZero)
                {
                    _stateProvider.AddToBalanceAndCreateIfNotExists(spec.Eip1559FeeCollector, burntFees, spec);
                }
            }

            if (txTracer.IsTracingFees)
            {
                txTracer.ReportFees(fees, burntFees);
            }
        }

        private void PrepareAccountForContractDeployment(Address contractAddress, IReleaseSpec spec)
        {
            if (_stateProvider.AccountExists(contractAddress))
            {
                CodeInfo codeInfo = _virtualMachine.GetCachedCodeInfo(_worldState, contractAddress, spec);
                bool codeIsNotEmpty = codeInfo.MachineCode.Length != 0;
                bool accountNonceIsNotZero = _stateProvider.GetNonce(contractAddress) != 0;

                // TODO: verify what should happen if code info is a precompile
                // (but this would generally be a hash collision)
                if (codeIsNotEmpty || accountNonceIsNotZero)
                {
                    if (_logger.IsTrace)
                    {
                        _logger.Trace($"Contract collision at {contractAddress}");
                    }

                    throw new TransactionCollisionException();
                }

                // we clean any existing storage (in case of a previously called self destruct)
                _stateProvider.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }

        private long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender,
            in UInt256 gasPrice, IReleaseSpec spec)
        {
            long spentGas = gasLimit;
            if (!substate.IsError)
            {
                spentGas -= unspentGas;
                long refund = substate.ShouldRevert
                    ? 0
                    : RefundHelper.CalculateClaimableRefund(spentGas,
                        substate.Refund + substate.DestroyList.Count * RefundOf.Destroy(spec.IsEip3529Enabled), spec);

                if (_logger.IsTrace)
                    _logger.Trace("Refunding unused gas of " + unspentGas + " and refund of " + refund);
                _stateProvider.AddToBalance(sender, (ulong)(unspentGas + refund) * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }
    }
}
