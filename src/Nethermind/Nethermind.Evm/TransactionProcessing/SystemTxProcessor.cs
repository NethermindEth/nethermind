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
using Nethermind.State.Tracing;
using static Nethermind.Core.Extensions.MemoryExtensions;
using static Nethermind.Evm.TransactionProcessing.ITransactionProcessor;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm.TransactionProcessing
{
    public class SystemTxProcessor : ITransactionProcessor
    {
        private readonly EthereumEcdsa _ecdsa;
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IWorldState _worldState;
        private readonly IVirtualMachine _virtualMachine;

        public SystemTxProcessor(
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

        public void CallAndRestore(Transaction transaction, BlockExecutionContext blkCtx, ITxTracer txTracer)
        {
            Execute(transaction, blkCtx, txTracer, ExecutionOptions.CommitAndRestore);
        }

        public void BuildUp(Transaction transaction, BlockExecutionContext blkCtx, ITxTracer txTracer)
        {
            // we need to treat the result of previous transaction as the original value of next transaction
            // when we do not commit
            _worldState.TakeSnapshot(true);
            Execute(transaction, blkCtx, txTracer, ExecutionOptions.None);
        }

        public void Execute(Transaction transaction, BlockExecutionContext blkCtx, ITxTracer txTracer)
        {
            Execute(transaction, blkCtx, txTracer, ExecutionOptions.Commit);
        }

        public void Trace(Transaction transaction, BlockExecutionContext blkCtx, ITxTracer txTracer)
        {
            Execute(transaction, blkCtx, txTracer, ExecutionOptions.NoValidation);
        }

        protected virtual void Execute(Transaction tx, BlockExecutionContext blkCtx, ITxTracer tracer, ExecutionOptions opts)
        {
            BlockHeader header = blkCtx.Header;
            IReleaseSpec spec = _specProvider.GetSpec(header);
            if (!spec.IsEip4788Enabled) // ToDo add comment
                spec = new SystemTransactionReleaseSpec(spec);

            // restore is CallAndRestore - previous call, we will restore state after the execution
            bool restore = opts.HasFlag(ExecutionOptions.Restore);
            // commit - is for standard execute, we will commit thee state after execution
            // !commit - is for build up during block production, we won't commit state after each transaction to support rollbacks
            // we commit only after all block is constructed
            bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;

            if (!ValidateStatic(tx, header, spec, tracer, opts, out long intrinsicGas))
                return;

            UInt256 effectiveGasPrice =
                tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, header.BaseFeePerGas);

            TransactionProcessor.RecordMetrics(opts, effectiveGasPrice);

            bool deleteCallerAccount = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);

            if (!ValidateSender(tx, header, spec, tracer, opts))
                return;

            if (!BuyGas(tx, header, spec, tracer, opts, effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment))
                return;

            if (commit)
                _worldState.Commit(spec, tracer.IsTracingState ? tracer : NullTxTracer.Instance);

            ExecutionEnvironment env = BuildExecutionEnvironmnet(tx, header, spec, tracer, opts, effectiveGasPrice);

            long gasAvailable = tx.GasLimit - intrinsicGas;
            if (!ExecuteEVMCall(tx, header, spec, tracer, opts, gasAvailable, env, out TransactionSubstate? substate, out long spentGas, out byte statusCode))
                return;

            // Finalize
            if (restore)
            {
                _worldState.Reset();
                if (deleteCallerAccount)
                {
                    _worldState.DeleteAccount(tx.SenderAddress);
                }
                else
                {
                    if (!opts.HasFlag(ExecutionOptions.NoValidation))
                        _worldState.AddToBalance(tx.SenderAddress, senderReservedGasPayment, spec);

                    _worldState.Commit(spec);
                }
            }
            else if (commit)
            {
                _worldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance);
            }

            if (tracer.IsTracingReceipt)
            {
                Keccak stateRoot = null;
                if (!spec.IsEip658Enabled)
                {
                    _worldState.RecalculateStateRoot();
                    stateRoot = _worldState.StateRoot;
                }

                if (statusCode == StatusCode.Failure)
                {
                    byte[] output = (substate?.ShouldRevert ?? false) ? substate.Output.ToArray() : Array.Empty<byte>();
                    tracer.MarkAsFailed(env.ExecutingAccount, spentGas, output, substate?.Error, stateRoot);
                }
                else
                {
                    LogEntry[] logs = substate.Logs.Any() ? substate.Logs.ToArray() : Array.Empty<LogEntry>();
                    tracer.MarkAsSuccess(env.ExecutingAccount, spentGas, substate.Output.ToArray(), logs, stateRoot);
                }
            }
        }

        private void QuickFail(Transaction tx, BlockHeader block, IReleaseSpec spec, ITxTracer txTracer, string? reason)
        {
            block.GasUsed += tx.GasLimit;

            Address recipient = tx.To ?? ContractAddress.From(
                tx.SenderAddress ?? Address.Zero,
                _worldState.GetNonce(tx.SenderAddress ?? Address.Zero));

            if (txTracer.IsTracingReceipt)
            {
                Keccak? stateRoot = null;
                if (!spec.IsEip658Enabled)
                {
                    _worldState.RecalculateStateRoot();
                    stateRoot = _worldState.StateRoot;
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
        /// <param name="header">The block containing the transaction. Only BaseFee is being used from the block atm.</param>
        /// <param name="spec">The release spec with which the transaction will be executed</param>
        /// <param name="tracer">The transaction tracer</param>
        /// <param name="opts">Options (Flags) to use for execution</param>
        /// <param name="premium">Computed premium per gas</param>
        /// <returns></returns>
        protected virtual bool ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts, out long intrinsicGas)
        {
            intrinsicGas = IntrinsicGasCalculator.Calculate(tx, spec);

            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (tx.SenderAddress is null)
            {
                TraceLogInvalidTx(tx, "SENDER_NOT_SPECIFIED");
                QuickFail(tx, header, spec, tracer, "sender not specified");
                return false;
            }

            if (validate && tx.Nonce >= ulong.MaxValue - 1)
            {
                // we are here if nonce is at least (ulong.MaxValue - 1). If tx is contract creation,
                // it is max possible value. Otherwise, (ulong.MaxValue - 1) is allowed, but ulong.MaxValue not.
                if (tx.IsContractCreation || tx.Nonce == ulong.MaxValue)
                {
                    TraceLogInvalidTx(tx, "NONCE_OVERFLOW");
                    QuickFail(tx, header, spec, tracer, "nonce overflow");
                    return false;
                }
            }

            if (tx.IsAboveInitCode(spec))
            {
                TraceLogInvalidTx(tx, $"CREATE_TRANSACTION_SIZE_EXCEEDS_MAX_INIT_CODE_SIZE {tx.DataLength} > {spec.MaxInitCodeSize}");
                QuickFail(tx, header, spec, tracer, "EIP-3860 - transaction size over max init code size");
                return false;
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

            if (!_worldState.AccountExists(tx.SenderAddress))
            {
                if (_logger.IsDebug) _logger.Debug($"TX sender account does not exist {tx.SenderAddress} - trying to recover it");

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
                        _worldState.CreateAccount(tx.SenderAddress, UInt256.Zero);
                    }
                }

                if (tx.SenderAddress is null)
                {
                    throw new InvalidDataException($"Failed to recover sender address on tx {tx.Hash} when previously recovered sender account did not exist.");
                }
            }

            return deleteCallerAccount;
        }


        protected virtual bool ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (validate && _worldState.IsInvalidContractSender(spec, tx.SenderAddress))
            {
                TraceLogInvalidTx(tx, "SENDER_IS_CONTRACT");
                QuickFail(tx, header, spec, tracer, "sender has deployed code");
                return false;
            }

            return true;
        }

        private bool BuyGas(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
                in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment)
        {
            premiumPerGas = UInt256.Zero;
            senderReservedGasPayment = UInt256.Zero;
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (validate)
                _worldState.SubtractFromBalance(tx.SenderAddress, senderReservedGasPayment, spec);

            return true;
        }

        protected virtual ExecutionEnvironment BuildExecutionEnvironmnet(
            Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
            in UInt256 effectiveGasPrice)
        {
            Address recipient = tx.GetRecipient(tx.IsContractCreation ? _worldState.GetNonce(tx.SenderAddress) : 0) ??
                // this transaction is not a contract creation so it should have the recipient known and not null
                throw new InvalidDataException("Recipient has not been resolved properly before tx execution");

            TxExecutionContext executionContext =
                new(header, tx.SenderAddress, effectiveGasPrice, tx.BlobVersionedHashes);

            CodeInfo codeInfo = tx.IsContractCreation ? new(tx.Data.AsArray())
                                    : _virtualMachine.GetCachedCodeInfo(_worldState, recipient, spec);

            byte[] inputData = tx.IsMessageCall ? tx.Data.AsArray() ?? Array.Empty<byte>() : Array.Empty<byte>();

            return new ExecutionEnvironment
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
        }

        protected virtual bool ExecuteEVMCall(
            Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
            in long gasAvailable, in ExecutionEnvironment env,
            out TransactionSubstate? substate, out long spentGas, out byte statusCode)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            substate = null;
            spentGas = tx.GasLimit;
            statusCode = StatusCode.Failure;

            long unspentGas = gasAvailable;

            Snapshot snapshot = _worldState.TakeSnapshot();

            // Fixes eth_estimateGas.
            // If sender is SystemUser subtracting value will cause InsufficientBalanceException
            if (validate)
                _worldState.SubtractFromBalance(tx.SenderAddress, tx.Value, spec);

            try
            {
                if (tx.IsContractCreation)
                {
                    // if transaction is a contract creation then recipient address is the contract deployment address
                    PrepareAccountForContractDeployment(env.ExecutingAccount, spec);
                }

                ExecutionType executionType =
                    tx.IsContractCreation ? ExecutionType.Create : ExecutionType.Transaction;

                using (EvmState state = new(unspentGas, env, executionType, true, snapshot, false))
                {
                    if (spec.UseTxAccessLists)
                    {
                        state.WarmUp(tx.AccessList); // eip-2930
                    }

                    if (spec.UseHotAndColdStorage)
                    {
                        state.WarmUp(tx.SenderAddress); // eip-2929
                        state.WarmUp(env.ExecutingAccount); // eip-2929
                    }

                    if (spec.AddCoinbaseToTxAccessList)
                    {
                        state.WarmUp(header.GasBeneficiary);
                    }

                    if (!tracer.IsTracingActions)
                    {
                        substate = _virtualMachine.Run<NotTracing>(state, _worldState, tracer);
                    }
                    else
                    {
                        substate = _virtualMachine.Run<IsTracing>(state, _worldState, tracer);
                    }

                    unspentGas = state.GasAvailable;

                    if (tracer.IsTracingAccess)
                    {
                        tracer.ReportAccess(state.AccessedAddresses, state.AccessedStorageCells);
                    }
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (_logger.IsTrace)
                        _logger.Trace("Restoring state from before transaction");
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
                            _worldState.InsertCode(env.ExecutingAccount, substate.Output, spec);
                            unspentGas -= codeDepositGasCost;
                        }
                    }

                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        if (_logger.IsTrace)
                            _logger.Trace($"Destroying account {toBeDestroyed}");

                        _worldState.ClearStorage(toBeDestroyed);
                        _worldState.DeleteAccount(toBeDestroyed);

                        if (tracer.IsTracingRefunds)
                            tracer.ReportRefund(RefundOf.Destroy(spec.IsEip3529Enabled));
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(tx.GasLimit, unspentGas, substate, tx.SenderAddress, env.TxExecutionContext.GasPrice, opts, spec);
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (_logger.IsTrace)
                    _logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}");
                _worldState.Restore(snapshot);
            }

            return true;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }

        protected long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender,
            in UInt256 gasPrice, ExecutionOptions opts, IReleaseSpec spec)
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
                if (!opts.HasFlag(ExecutionOptions.NoValidation))
                    _worldState.AddToBalance(sender, (ulong)(unspentGas + refund) * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }
    }
}
