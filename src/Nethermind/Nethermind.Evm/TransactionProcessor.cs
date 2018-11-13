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
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
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
        }

        private TransactionReceipt GetNullReceipt(int index, BlockHeader block, Transaction transaction, Address recipient)
        {
            return BuildTransactionReceipt(index, block, transaction, (long)transaction.GasLimit, StatusCode.Failure, LogEntry.EmptyLogs, recipient);
        }

        [Todo("Wider work needed to split calls and execution properly")]
        public (TransactionReceipt, TransactionTrace) CallAndRestore(int index, Transaction transaction, BlockHeader block, bool shouldTrace)
        {
            return Execute(index, transaction, block, shouldTrace, true);
        }

        public (TransactionReceipt, TransactionTrace) Execute(int index, Transaction transaction, BlockHeader block, bool shouldTrace)
        {
            return Execute(index, transaction, block, shouldTrace, false);
        }
        
        public (TransactionReceipt, TransactionTrace) Execute(int index, Transaction transaction, BlockHeader block, bool shouldTrace, bool readOnly)
        {
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
                return (GetNullReceipt(index, block, transaction, recipient), TransactionTrace.QuickFail);
            }

            long intrinsicGas = _intrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsTrace) _logger.Trace($"Intrinsic gas calculated for {transaction.Hash}: " + intrinsicGas);

            if (gasLimit < intrinsicGas)
            {
                TraceLogInvalidTx(transaction, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                return (GetNullReceipt(index, block, transaction, recipient), TransactionTrace.QuickFail);
            }

            if (gasLimit > block.GasLimit - block.GasUsed)
            {
                TraceLogInvalidTx(transaction, $"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GasLimit} - {block.GasUsed}");
                return (GetNullReceipt(index, block, transaction, recipient), TransactionTrace.QuickFail);
            }

            if (!_stateProvider.AccountExists(sender))
            {
                TraceLogInvalidTx(transaction, $"SENDER_ACCOUNT_DOES_NOT_EXIST {sender}");
                if (gasPrice == UInt256.Zero)
                {
                    _stateProvider.CreateAccount(sender, UInt256.Zero);
                }
            }

            UInt256 senderBalance = _stateProvider.GetBalance(sender);
            if ((ulong) intrinsicGas * gasPrice + value > senderBalance)
            {
                TraceLogInvalidTx(transaction, $"INSUFFICIENT_SENDER_BALANCE: ({sender})_BALANCE = {senderBalance}");
                return (GetNullReceipt(index, block, transaction, recipient), TransactionTrace.QuickFail);
            }

            if (transaction.Nonce != _stateProvider.GetNonce(sender))
            {
                TraceLogInvalidTx(transaction, $"WRONG_TRANSACTION_NONCE: {transaction.Nonce} (expected {_stateProvider.GetNonce(sender)})");
                return (GetNullReceipt(index, block, transaction, recipient), TransactionTrace.QuickFail);
            }

            _stateProvider.IncrementNonce(sender);
            _stateProvider.SubtractFromBalance(sender, (ulong) gasLimit * gasPrice, spec);
            _stateProvider.Commit(_specProvider.GetSpec(block.Number));
            

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
                env.ExecutingAccount = recipient;
                env.CurrentBlock = block;
                env.GasPrice = gasPrice;
                env.InputData = data ?? new byte[0];
                env.CodeInfo = isPrecompile ? new CodeInfo(recipient) : machineCode == null ? _virtualMachine.GetCachedCodeInfo(recipient) : new CodeInfo(machineCode);
                env.Originator = sender;

                ExecutionType executionType = isPrecompile
                    ? ExecutionType.DirectPrecompile
                    : transaction.IsContractCreation
                        ? ExecutionType.DirectCreate
                        : ExecutionType.Transaction;

                using (EvmState state = new EvmState(unspentGas, env, executionType, false))
                {
                    substate = _virtualMachine.Run(state, spec, shouldTrace);
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
                        long codeDepositGasCost = substate.Output.Length * GasCostOf.CodeDeposit;
                        if (spec.IsEip170Enabled && substate.Output.Length > 0x6000)
                        {
                            codeDepositGasCost = long.MaxValue;
                        }

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
                if (!_stateProvider.AccountExists(gasBeneficiary))
                {
                    _stateProvider.CreateAccount(gasBeneficiary, (ulong) spentGas * gasPrice);
                }
                else
                {
                    _stateProvider.AddToBalance(gasBeneficiary, (ulong) spentGas * gasPrice, spec);
                }
            }

            if (!readOnly)
            {
                _storageProvider.Commit(spec);
                _stateProvider.Commit(spec);
            }
            else
            {
                _storageProvider.Reset();
                _stateProvider.Reset();
            }

            if (!readOnly)
            {
                block.GasUsed += spentGas;
            }

            if (substate?.Trace != null)
            {
                substate.Trace.ReturnValue = substate.Output?.ToHexString();
            }

            return (BuildTransactionReceipt(index, block, transaction, spentGas, statusCode, (statusCode == StatusCode.Success && substate.Logs.Any()) ? substate.Logs.ToArray() : LogEntry.EmptyLogs, recipient), substate?.Trace ?? TransactionTrace.QuickFail);
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

            if (substate.Trace != null)
            {
                substate.Trace.Gas = spentGas;
            }

            return spentGas;
        }

        private TransactionReceipt BuildTransactionReceipt(int index, BlockHeader block, Transaction transaction, long spentGas, byte statusCode, LogEntry[] logEntries, Address recipient)
        {
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = logEntries;
            transactionReceipt.Bloom = logEntries.Length == 0 ? Bloom.Empty : BuildBloom(logEntries);
            transactionReceipt.GasUsedTotal = block.GasUsed;
            if (!_specProvider.GetSpec(block.Number).IsEip658Enabled)
            {
                transactionReceipt.PostTransactionState = _stateProvider.StateRoot;
            }

            transactionReceipt.StatusCode = statusCode;
            transactionReceipt.Recipient = transaction.IsContractCreation ? null : recipient;
            
            transactionReceipt.BlockHash = block.Hash;
            transactionReceipt.BlockNumber = block.Number;
            transactionReceipt.Index = index;
            transactionReceipt.GasUsed = spentGas;
            transactionReceipt.Sender = transaction.SenderAddress;
            transactionReceipt.ContractAddress = transaction.IsContractCreation ? recipient : null;
            transactionReceipt.TransactionHash = transaction.Hash;
            
            return transactionReceipt;
        }

        public static Bloom BuildBloom(TransactionReceipt[] receipts)
        {
            Bloom bloom = new Bloom();
            for (int i = 0; i < receipts.Length; i++)
            {
                AddToBloom(bloom, receipts[i].Logs);
            }
            
            return bloom;
        }
        
        public static Bloom BuildBloom(LogEntry[] logEntries)
        {
            Bloom bloom = new Bloom();
            AddToBloom(bloom, logEntries);
            return bloom;
        }

        public static void AddToBloom(Bloom bloom, LogEntry[] logEntries)
        {
            for (int entryIndex = 0; entryIndex < logEntries.Length; entryIndex++)
            {
                LogEntry logEntry = logEntries[entryIndex];
                byte[] addressBytes = logEntry.LoggersAddress.Bytes;
                bloom.Set(addressBytes);
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    Keccak topic = logEntry.Topics[topicIndex];
                    bloom.Set(topic.Bytes);
                }
            }
        }
    }
}