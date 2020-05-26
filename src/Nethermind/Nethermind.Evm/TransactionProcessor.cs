//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
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
            _ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
        }

        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block, txTracer, true);
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block, txTracer, false);
        }

        private void QuickFail(Transaction tx, BlockHeader block, ITxTracer txTracer, string reason)
        {
            block.GasUsed += tx.GasLimit;
            Address recipient = tx.To ?? ContractAddress.From(tx.SenderAddress, _stateProvider.GetNonce(tx.SenderAddress));

            _stateProvider.RecalculateStateRoot();
            Keccak stateRoot = _specProvider.GetSpec(block.Number).IsEip658Enabled ? null : _stateProvider.StateRoot;
            if (txTracer.IsTracingReceipt) txTracer.MarkAsFailed(recipient, tx.GasLimit, Bytes.Empty, reason ?? "invalid", stateRoot);
        }

        private EthereumEcdsa _ecdsa;

        private void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer, bool isCall)
        {
            var notSystemTransaction = !transaction.IsSystem();
            var wasSenderAccountCreatedInsideACall = false;
            
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            if (!notSystemTransaction)
            {
                spec = new SystemTransactionReleaseSpec(spec);
            }

            Address recipient = transaction.To;
            UInt256 value = transaction.Value;
            UInt256 gasPrice = transaction.GasPrice;
            long gasLimit = transaction.GasLimit;
            byte[] machineCode = transaction.Init;
            byte[] data = transaction.Data ?? Bytes.Empty;

            Address sender = transaction.SenderAddress;
            if (_logger.IsTrace) _logger.Trace($"Executing tx {transaction.Hash}");

            if (sender == null)
            {
                TraceLogInvalidTx(transaction, "SENDER_NOT_SPECIFIED");
                QuickFail(transaction, block, txTracer, "sender not specified");
                return;
            }

            long intrinsicGas = _intrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsTrace) _logger.Trace($"Intrinsic gas calculated for {transaction.Hash}: " + intrinsicGas);

            if (notSystemTransaction)
            {
                if (gasLimit < intrinsicGas)
                {
                    TraceLogInvalidTx(transaction, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                    QuickFail(transaction, block, txTracer, "gas limit below intrinsic gas");
                    return;
                }

                if (!isCall && gasLimit > block.GasLimit - block.GasUsed)
                {
                    TraceLogInvalidTx(transaction,
                        $"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GasLimit} - {block.GasUsed}");
                    QuickFail(transaction, block, txTracer, "block gas limit exceeded");
                    return;
                }
            }

            if (!_stateProvider.AccountExists(sender))
            {
                // hacky fix for the potential recovery issue
                if (transaction.Signature != null)
                {
                    transaction.SenderAddress = _ecdsa.RecoverAddress(transaction);
                }

                if (sender != transaction.SenderAddress)
                {
                    if (_logger.IsWarn) _logger.Warn($"TX recovery issue fixed - tx was coming with sender {sender} and the now it recovers to {transaction.SenderAddress}");
                    sender = transaction.SenderAddress;
                }
                else
                {
                    TraceLogInvalidTx(transaction, $"SENDER_ACCOUNT_DOES_NOT_EXIST {sender}");
                    if (isCall || gasPrice == UInt256.Zero)
                    {
                        wasSenderAccountCreatedInsideACall = isCall;
                        _stateProvider.CreateAccount(sender, UInt256.Zero);
                    }
                }
            }

            if (notSystemTransaction)
            {
                UInt256 senderBalance = _stateProvider.GetBalance(sender);
                if (!isCall && (ulong) intrinsicGas * gasPrice + value > senderBalance)
                {
                    TraceLogInvalidTx(transaction, $"INSUFFICIENT_SENDER_BALANCE: ({sender})_BALANCE = {senderBalance}");
                    QuickFail(transaction, block, txTracer, "insufficient sender balance");
                    return;
                }

                if (transaction.Nonce != _stateProvider.GetNonce(sender))
                {
                    TraceLogInvalidTx(transaction, $"WRONG_TRANSACTION_NONCE: {transaction.Nonce} (expected {_stateProvider.GetNonce(sender)})");
                    QuickFail(transaction, block, txTracer, "wrong transaction nonce");
                    return;
                }

                _stateProvider.IncrementNonce(sender);
            }

            UInt256 senderReservedGasPayment = isCall ? UInt256.Zero : (ulong) gasLimit * gasPrice;
            
            _stateProvider.SubtractFromBalance(sender, senderReservedGasPayment, spec);
            _stateProvider.Commit(spec, txTracer.IsTracingState ? txTracer : null);

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
                    recipient = transaction.IsSystem() 
                        ? transaction.SenderAddress
                        : ContractAddress.From(sender, _stateProvider.GetNonce(sender) - 1);

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
                using (EvmState state = new EvmState(unspentGas, env, executionType, isPrecompile, true, false))
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
                        _storageProvider.ClearStorage(toBeDestroyed);
                        _stateProvider.DeleteAccount(toBeDestroyed);
                        if (txTracer.IsTracingRefunds) txTracer.ReportRefund(RefundOf.Destroy);
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

            if (!isCall)
            {
                _storageProvider.Commit(txTracer.IsTracingState ? txTracer : null);
                _stateProvider.Commit(spec, txTracer.IsTracingState ? txTracer : null);
            }
            else
            {
                _storageProvider.Reset();
                _stateProvider.Reset();
                
                if (wasSenderAccountCreatedInsideACall)
                {
                    _stateProvider.DeleteAccount(sender);
                }
                else
                {
                    _stateProvider.AddToBalance(sender, senderReservedGasPayment, spec);
                    _stateProvider.DecrementNonce(sender);    
                }
                
                _stateProvider.Commit(spec);
            }

            if (!isCall && notSystemTransaction)
            {
                block.GasUsed += spentGas;
            }

            if (txTracer.IsTracingReceipt)
            {
                Keccak stateRoot = null;
                bool eip658Enabled = _specProvider.GetSpec(block.Number).IsEip658Enabled;
                if (!eip658Enabled)
                {
                    _stateProvider.RecalculateStateRoot();
                    stateRoot = _stateProvider.StateRoot;
                }

                if (statusCode == StatusCode.Failure)
                {
                    txTracer.MarkAsFailed(recipient, spentGas, (substate?.ShouldRevert ?? false) ? substate.Output : Bytes.Empty, substate?.Error, stateRoot);
                }
                else
                {
                    txTracer.MarkAsSuccess(recipient, spentGas, substate.Output, substate.Logs.Any() ? substate.Logs.ToArray() : LogEntry.EmptyLogs, stateRoot);
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
                long refund = substate.ShouldRevert ? 0 : RefundHelper.CalculateClaimableRefund(spentGas, substate.Refund + substate.DestroyList.Count * RefundOf.Destroy);

                if (_logger.IsTrace) _logger.Trace("Refunding unused gas of " + unspentGas + " and refund of " + refund);
                _stateProvider.AddToBalance(sender, (ulong) (unspentGas + refund) * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }
    }
}