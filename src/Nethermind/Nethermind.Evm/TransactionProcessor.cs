/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Evm
{
    public class TransactionProcessor : ITransactionProcessor
    {
        private readonly IntrinsicGasCalculator _intrinsicGasCalculator = new IntrinsicGasCalculator();
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IVirtualMachine _virtualMachine;

        public TransactionProcessor(ISpecProvider specProvider, IStateProvider stateProvider, IStorageProvider storageProvider, IVirtualMachine virtualMachine, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _virtualMachine = virtualMachine ?? throw new ArgumentNullException(nameof(virtualMachine));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _ecdsa = new EthereumEcdsa(specProvider, logManager);
        }

        [Todo("Wider work needed to split calls and execution properly")]
        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block.AsStateUpdate(), txTracer, true);
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block.AsStateUpdate(), txTracer, false);
        }

        private void QuickFail(Transaction tx, StateUpdate block, ITxTracer txTracer, bool readOnly)
        {
            block.GasUsed += (long) tx.GasLimit;
            Address recipient = tx.To ?? Address.OfContract(tx.SenderAddress, _stateProvider.GetNonce(tx.SenderAddress));
            if (txTracer.IsTracingReceipt) txTracer.MarkAsFailed(recipient, (long) tx.GasLimit, Bytes.Empty, "invalid");
        }

        private EthereumEcdsa _ecdsa;

        private void Execute(Transaction transaction, StateUpdate block, ITxTracer txTracer, bool readOnly)
        {
            var notSystemTransaction = !transaction.IsSystem();
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            Address recipient = transaction.To;
            UInt256 value = transaction.Value;
            UInt256 gasPrice = transaction.GasPrice;
            long gasLimit = (long) transaction.GasLimit;
            byte[] machineCode = transaction.Init;
            byte[] data = transaction.Data ?? Bytes.Empty;

            Address sender = transaction.SenderAddress;
            if (_logger.IsTrace) _logger.Trace($"Executing tx {transaction.Hash}");

            if (sender == null)
            {
                TraceLogInvalidTx(transaction, "SENDER_NOT_SPECIFIED");
                QuickFail(transaction, block, txTracer, readOnly);
                return;
            }

            long intrinsicGas = _intrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsTrace) _logger.Trace($"Intrinsic gas calculated for {transaction.Hash}: " + intrinsicGas);

            if (notSystemTransaction)
            {
                if (gasLimit < intrinsicGas)
                {
                    TraceLogInvalidTx(transaction, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                    QuickFail(transaction, block, txTracer, readOnly);
                    return;
                }

                if (gasLimit > block.GasLimit - block.GasUsed)
                {
                    TraceLogInvalidTx(transaction,
                        $"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GasLimit} - {block.GasUsed}");
                    QuickFail(transaction, block, txTracer, readOnly);
                    return;
                }
            }

            if (!_stateProvider.AccountExists(sender))
            {
                // hacky fix for the potential recovery issue
                if (transaction.Signature != null)
                {
                    transaction.SenderAddress = _ecdsa.RecoverAddress(transaction, block.Number);
                }
                
                if (sender != transaction.SenderAddress)
                {
                    if(_logger.IsWarn) _logger.Warn($"TX recovery issue fixed - tx was coming with sender {sender} and the now it recovers to {transaction.SenderAddress}");
                    sender = transaction.SenderAddress;
                }
                else
                {
                    TraceLogInvalidTx(transaction, $"SENDER_ACCOUNT_DOES_NOT_EXIST {sender}");
                    if (gasPrice == UInt256.Zero)
                    {
                        _stateProvider.CreateAccount(sender, UInt256.Zero);
                    }                    
                }
            }

            if (notSystemTransaction)
            {
                UInt256 senderBalance = _stateProvider.GetBalance(sender);
                if ((ulong) intrinsicGas * gasPrice + value > senderBalance)
                {
                    TraceLogInvalidTx(transaction, $"INSUFFICIENT_SENDER_BALANCE: ({sender})_BALANCE = {senderBalance}");
                    QuickFail(transaction, block, txTracer, readOnly);
                    return;
                }

                if (transaction.Nonce != _stateProvider.GetNonce(sender))
                {
                    TraceLogInvalidTx(transaction, $"WRONG_TRANSACTION_NONCE: {transaction.Nonce} (expected {_stateProvider.GetNonce(sender)})");
                    QuickFail(transaction, block, txTracer, readOnly);
                    return;
                }

                _stateProvider.IncrementNonce(sender);
            }

            _stateProvider.SubtractFromBalance(sender, (ulong) gasLimit * gasPrice, spec);

            // TODO: I think we can skip this commit and decrease the tree operations this way
            _stateProvider.Commit(_specProvider.GetSpec(block.Number), txTracer.IsTracingState ? txTracer : null);

            long unspentGas = gasLimit - intrinsicGas;
            long spentGas = gasLimit;

            int stateSnapshot = _stateProvider.TakeSnapshot();
            int storageSnapshot = _storageProvider.TakeSnapshot();

            _stateProvider.SubtractFromBalance(sender, value, spec);
            byte statusCode = StatusCode.Failure;
            TransactionSubstate substate = null;

            try
            {
                if (transaction.IsContractCreation)
                {
                    recipient = Address.OfContract(sender, _stateProvider.GetNonce(sender) - 1);
                    if (transaction.IsSystem())
                    {
                        recipient = transaction.SenderAddress;
                    }
                    
                    if (_stateProvider.AccountExists(recipient))
                    {
                        if ((_virtualMachine.GetCachedCodeInfo(recipient)?.MachineCode?.Length ?? 0) != 0 || _stateProvider.GetNonce(recipient) != 0)
                        {
                            if (_logger.IsTrace)
                            {
                                _logger.Trace($"Contract collision at {recipient}"); // the account already owns the contract with the code
                            }

                            throw new TransactionCollisionException();
                        }

                        _stateProvider.UpdateStorageRoot(recipient, Keccak.EmptyTreeHash);
                    }
                }

                bool isPrecompile = recipient.IsPrecompiled(spec);

                ExecutionEnvironment env = new ExecutionEnvironment();
                env.Value = value;
                env.TransferValue = value;
                env.Sender = sender;
                env.CodeSource = recipient;
                env.ExecutingAccount = recipient;
                env.CurrentBlock = block;
                env.GasPrice = gasPrice;
                env.InputData = data ?? new byte[0];
                env.CodeInfo = isPrecompile ? new CodeInfo(recipient) : machineCode == null ? _virtualMachine.GetCachedCodeInfo(recipient) : new CodeInfo(machineCode);
                env.Originator = sender;

                ExecutionType executionType = transaction.IsContractCreation ? ExecutionType.Create : ExecutionType.Call;
                using (VmState state = new VmState(unspentGas, env, executionType, isPrecompile, true, false))
                {
                    substate = _virtualMachine.Run(state, txTracer);
                    unspentGas = state.GasAvailable;
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (_logger.IsTrace) _logger.Trace("Restoring state from before transaction");
                    _stateProvider.Restore(stateSnapshot);
                    _storageProvider.Restore(storageSnapshot);
                }
                else
                {
                    // tks: there is similar code fo contract creation from init and from CREATE
                    // this may lead to inconsistencies (however it is tested extensively in blockchain tests)
                    if (transaction.IsContractCreation)
                    {
                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(substate.Output.Length, spec);
                        if (unspentGas < codeDepositGasCost && spec.IsEip2Enabled)
                        {
                            throw new OutOfGasException();
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
                        _stateProvider.DeleteAccount(toBeDestroyed);
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(gasLimit, unspentGas, substate, sender, gasPrice, spec);
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (_logger.IsTrace) _logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}");
                _stateProvider.Restore(stateSnapshot);
                _storageProvider.Restore(storageSnapshot);
            }

            if (_logger.IsTrace) _logger.Trace("Gas spent: " + spentGas);

            Address gasBeneficiary = block.GasBeneficiary;
            if (statusCode == StatusCode.Failure || !(substate?.DestroyList.Contains(gasBeneficiary) ?? false))
            {
                if (notSystemTransaction)
                {
                    if (!_stateProvider.AccountExists(gasBeneficiary))
                    {
                        _stateProvider.CreateAccount(gasBeneficiary, (ulong) spentGas * gasPrice);
                    }
                    else
                    {
                        _stateProvider.AddToBalance(gasBeneficiary, (ulong) spentGas * gasPrice, spec);
                    }
                }
            }

            if (!readOnly)
            {
                _storageProvider.Commit(txTracer.IsTracingState ? txTracer : null);
                _stateProvider.Commit(spec, txTracer.IsTracingState ? txTracer : null);
            }
            else
            {
                _storageProvider.Reset();
                _stateProvider.Reset();
            }

            if (!readOnly && notSystemTransaction)
            {
                block.GasUsed += spentGas;
            }

            if (txTracer.IsTracingReceipt)
            {
                if (statusCode == StatusCode.Failure)
                {
                    txTracer.MarkAsFailed(recipient, spentGas, (substate?.ShouldRevert ?? false) ? substate.Output : Bytes.Empty, substate?.Error);
                }
                else
                {
                    txTracer.MarkAsSuccess(recipient, spentGas, substate.Output, substate.Logs.Any() ? substate.Logs.ToArray() : LogEntry.EmptyLogs);
                }
            }
        }

        private void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }

        private long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender, UInt256 gasPrice, IReleaseSpec spec)
        {
            long spentGas = gasLimit;
            if (!substate.IsError)
            {
                spentGas -= unspentGas;
                long refund = substate.ShouldRevert ? 0 : Math.Min(spentGas / 2L, substate.Refund + substate.DestroyList.Count * RefundOf.Destroy);

                if (_logger.IsTrace) _logger.Trace("Refunding unused gas of " + unspentGas + " and refund of " + refund);
                _stateProvider.AddToBalance(sender, (ulong) (unspentGas + refund) * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }
    }
}