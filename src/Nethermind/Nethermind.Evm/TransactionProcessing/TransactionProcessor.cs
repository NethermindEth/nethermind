// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using static Nethermind.Core.Extensions.MemoryExtensions;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Tracing;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Evm.TransactionProcessing
{
    public class TransactionProcessor : ITransactionProcessor
    {
        private readonly EthereumEcdsa _ecdsa;
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IWorldState _worldState;
        private readonly IVirtualMachine _virtualMachine;

        [Flags]
        private enum ExecutionOptions
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
            IWorldState? worldState,
            IVirtualMachine? virtualMachine,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
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

        private void QuickFail(Transaction tx, BlockHeader block, ITxTracer txTracer, bool eip658NotEnabled,
            string? reason)
        {
            block.GasUsed += tx.GasLimit;

            Address recipient = tx.To ?? ContractAddress.From(
                tx.SenderAddress ?? Address.Zero,
                _worldState.GetNonce(tx.SenderAddress ?? Address.Zero));

            if (txTracer.IsTracingReceipt)
            {
                Keccak? stateRoot = null;
                if (eip658NotEnabled)
                {
                    _worldState.RecalculateStateRoot();
                    stateRoot = _worldState.StateRoot;
                }

                txTracer.MarkAsFailed(recipient, tx.GasLimit, Array.Empty<byte>(), reason ?? "invalid", stateRoot);
            }
        }

        private void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer,
            ExecutionOptions executionOptions)
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
                QuickFail(transaction, block, txTracer, eip658NotEnabled, "miner premium is negative");
                return;
            }

            UInt256 effectiveGasPrice =
                transaction.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, block.BaseFeePerGas);

            long gasLimit = transaction.GasLimit;
            byte[] machineCode = transaction.IsContractCreation ? transaction.Data.AsArray() : null;
            byte[] data = transaction.IsMessageCall ? transaction.Data.AsArray() : Array.Empty<byte>();

            Address? caller = transaction.SenderAddress;
            if (_logger.IsTrace) _logger.Trace($"Executing tx {transaction.Hash}");

            if (caller is null)
            {
                TraceLogInvalidTx(transaction, "SENDER_NOT_SPECIFIED");
                QuickFail(transaction, block, txTracer, eip658NotEnabled, "sender not specified");
                return;
            }

            if (!noValidation && _worldState.IsInvalidContractSender(spec, caller))
            {
                TraceLogInvalidTx(transaction, "SENDER_IS_CONTRACT");
                QuickFail(transaction, block, txTracer, eip658NotEnabled, "sender has deployed code");
                return;
            }

            if (!noValidation && transaction.Nonce >= ulong.MaxValue - 1)
            {
                // we are here if nonce is at least (ulong.MaxValue - 1). If tx is contract creation,
                // it is max possible value. Otherwise, (ulong.MaxValue - 1) is allowed, but ulong.MaxValue not.
                if (transaction.IsContractCreation || transaction.Nonce == ulong.MaxValue)
                {
                    TraceLogInvalidTx(transaction, "NONCE_OVERFLOW");
                    QuickFail(transaction, block, txTracer, eip658NotEnabled, "nonce overflow");
                    return;
                }
            }

            if (transaction.IsAboveInitCode(spec))
            {
                TraceLogInvalidTx(transaction, $"CREATE_TRANSACTION_SIZE_EXCEEDS_MAX_INIT_CODE_SIZE {transaction.DataLength} > {spec.MaxInitCodeSize}");
                QuickFail(transaction, block, txTracer, eip658NotEnabled, "EIP-3860 - transaction size over max init code size");
                return;
            }

            long intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsTrace) _logger.Trace($"Intrinsic gas calculated for {transaction.Hash}: " + intrinsicGas);

            if (notSystemTransaction)
            {
                if (gasLimit < intrinsicGas)
                {
                    TraceLogInvalidTx(transaction, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                    QuickFail(transaction, block, txTracer, eip658NotEnabled, "gas limit below intrinsic gas");
                    return;
                }

                if (!noValidation && gasLimit > block.GasLimit - block.GasUsed)
                {
                    TraceLogInvalidTx(transaction,
                        $"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GasLimit} - {block.GasUsed}");
                    QuickFail(transaction, block, txTracer, eip658NotEnabled, "block gas limit exceeded");
                    return;
                }

                if (executionOptions == ExecutionOptions.Commit || executionOptions == ExecutionOptions.None)
                {
                    decimal gasPrice = (decimal)effectiveGasPrice / 1_000_000_000m;
                    Metrics.MinGasPrice = Math.Min(gasPrice, Metrics.MinGasPrice);
                    Metrics.MaxGasPrice = Math.Max(gasPrice, Metrics.MaxGasPrice);

                    Metrics.BlockMinGasPrice = Math.Min(gasPrice, Metrics.BlockMinGasPrice);
                    Metrics.BlockMaxGasPrice = Math.Max(gasPrice, Metrics.BlockMaxGasPrice);

                    Metrics.AveGasPrice = (Metrics.AveGasPrice * Metrics.Transactions + gasPrice) / (Metrics.Transactions + 1);
                    Metrics.EstMedianGasPrice += Metrics.AveGasPrice * 0.01m * decimal.Sign(gasPrice - Metrics.EstMedianGasPrice);
                    Metrics.Transactions++;

                    Metrics.BlockAveGasPrice = (Metrics.BlockAveGasPrice * Metrics.BlockTransactions + gasPrice) / (Metrics.BlockTransactions + 1);
                    Metrics.BlockEstMedianGasPrice += Metrics.BlockAveGasPrice * 0.01m * decimal.Sign(gasPrice - Metrics.BlockEstMedianGasPrice);
                    Metrics.BlockTransactions++;
                }
            }

            if (!_worldState.AccountExists(caller))
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
                        _worldState.CreateAccount(caller, UInt256.Zero);
                    }
                }

                if (caller is null)
                {
                    throw new InvalidDataException(
                        $"Failed to recover sender address on tx {transaction.Hash} when previously recovered sender account did not exist.");
                }
            }

            UInt256 senderReservedGasPayment = (ulong)gasLimit * effectiveGasPrice;

            if (notSystemTransaction)
            {
                UInt256 senderBalance = _worldState.GetBalance(caller);
                if (!noValidation && ((ulong)intrinsicGas * effectiveGasPrice + value > senderBalance ||
                                      senderReservedGasPayment + value > senderBalance))
                {
                    TraceLogInvalidTx(transaction,
                        $"INSUFFICIENT_SENDER_BALANCE: ({caller})_BALANCE = {senderBalance}");
                    QuickFail(transaction, block, txTracer, eip658NotEnabled, "insufficient sender balance");
                    return;
                }

                if (!noValidation && spec.IsEip1559Enabled && !transaction.IsFree() &&
                    senderBalance < (UInt256)transaction.GasLimit * transaction.MaxFeePerGas + value)
                {
                    TraceLogInvalidTx(transaction,
                        $"INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({caller})_BALANCE = {senderBalance}, MAX_FEE_PER_GAS: {transaction.MaxFeePerGas}");
                    QuickFail(transaction, block, txTracer, eip658NotEnabled,
                        "insufficient MaxFeePerGas for sender balance");
                    return;
                }

                if (transaction.Nonce != _worldState.GetNonce(caller))
                {
                    TraceLogInvalidTx(transaction,
                        $"WRONG_TRANSACTION_NONCE: {transaction.Nonce} (expected {_worldState.GetNonce(caller)})");
                    QuickFail(transaction, block, txTracer, eip658NotEnabled, "wrong transaction nonce");
                    return;
                }

                _worldState.IncrementNonce(caller);
            }

            // Do not charge gas if noValidation is set
            if (!noValidation) _worldState.SubtractFromBalance(caller, senderReservedGasPayment, spec);
            if (commit)
            {
                _worldState.Commit(spec, txTracer.IsTracingState ? txTracer : NullTxTracer.Instance);
            }

            long unspentGas = gasLimit - intrinsicGas;
            long spentGas = gasLimit;

            Snapshot snapshot = _worldState.TakeSnapshot();
            // Fixes eth_estimateGas. If sender is systemUser subtracting value will cause InsufficientBalanceException
            if (!noValidation || notSystemTransaction) _worldState.SubtractFromBalance(caller, value, spec);
            byte statusCode = StatusCode.Failure;
            TransactionSubstate substate = null;

            Address? recipientOrNull = null;
            try
            {
                Address? recipient =
                    transaction.GetRecipient(transaction.IsContractCreation ? _worldState.GetNonce(caller) : 0);
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
                            _worldState.InsertCode(recipient, substate.Output, spec);
                            unspentGas -= codeDepositGasCost;
                        }
                    }

                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Destroying account {toBeDestroyed}");
                        _worldState.ClearStorage(toBeDestroyed);
                        _worldState.DeleteAccount(toBeDestroyed);
                        if (txTracer.IsTracingRefunds) txTracer.ReportRefund(RefundOf.Destroy(spec.IsEip3529Enabled));
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(gasLimit, unspentGas, substate, caller, effectiveGasPrice, noValidation, spec);
            }
            catch (Exception ex) when (
                ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
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
                    UInt256 fees = (ulong)spentGas * premiumPerGas;
                    if (_worldState.AccountExists(gasBeneficiary))
                    {
                        _worldState.AddToBalance(gasBeneficiary, fees, spec);
                    }
                    else
                    {
                        _worldState.CreateAccount(gasBeneficiary, fees);
                    }

                    UInt256 burntFees = !transaction.IsFree() ? (ulong)spentGas * block.BaseFeePerGas : 0;

                    if (spec.IsEip1559Enabled && spec.Eip1559FeeCollector is not null)
                    {
                        if (!burntFees.IsZero)
                        {
                            if (_worldState.AccountExists(spec.Eip1559FeeCollector))
                            {
                                _worldState.AddToBalance(spec.Eip1559FeeCollector, burntFees, spec);
                            }
                            else
                            {
                                _worldState.CreateAccount(spec.Eip1559FeeCollector, burntFees);
                            }
                        }
                    }

                    if (txTracer.IsTracingFees)
                    {
                        txTracer.ReportFees(fees, burntFees);
                    }
                }
            }

            if (restore)
            {
                _worldState.Reset();
                if (deleteCallerAccount)
                {
                    _worldState.DeleteAccount(caller);
                }
                else
                {
                    if (!noValidation) _worldState.AddToBalance(caller, senderReservedGasPayment, spec);
                    if (notSystemTransaction)
                    {
                        _worldState.DecrementNonce(caller);
                    }

                    _worldState.Commit(spec);
                }
            }
            else if (commit)
            {
                _worldState.Commit(spec, txTracer.IsTracingState ? txTracer : NullStateTracer.Instance);
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
                    _worldState.RecalculateStateRoot();
                    stateRoot = _worldState.StateRoot;
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

        private void PrepareAccountForContractDeployment(Address contractAddress, IReleaseSpec spec)
        {
            if (_worldState.AccountExists(contractAddress))
            {
                CodeInfo codeInfo = _virtualMachine.GetCachedCodeInfo(_worldState, contractAddress, spec);
                bool codeIsNotEmpty = codeInfo.MachineCode.Length != 0;
                bool accountNonceIsNotZero = _worldState.GetNonce(contractAddress) != 0;

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
                _worldState.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
            }
        }

        private void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }

        private long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender,
            in UInt256 gasPrice, bool noValidation, IReleaseSpec spec)
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
                // If noValidation we didn't charge for gas, so do not refund
                if (!noValidation) _worldState.AddToBalance(sender, (ulong)(unspentGas + refund) * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }
    }
}
