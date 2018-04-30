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
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Store;

namespace Nethermind.Evm
{
    public class TransactionProcessor : ITransactionProcessor
    {
        private readonly IntrinsicGasCalculator _intrinsicGasCalculator;
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IVirtualMachine _virtualMachine;
        private readonly IEthereumSigner _signer;

        public TransactionProcessor(ISpecProvider specProvider, IStateProvider stateProvider, IStorageProvider storageProvider, IVirtualMachine virtualMachine, IEthereumSigner signer, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _specProvider = specProvider;
            _virtualMachine = virtualMachine;
            _signer = signer;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _intrinsicGasCalculator = new IntrinsicGasCalculator();
        }

        private TransactionReceipt GetNullReceipt(BlockHeader block, long gasUsed)
        {
            block.GasUsed += gasUsed;
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = new LogEntry[0];
            transactionReceipt.Bloom = new Bloom();
            transactionReceipt.GasUsed = block.GasUsed;
            transactionReceipt.PostTransactionState = _stateProvider.StateRoot;
            transactionReceipt.StatusCode = StatusCode.Failure;
            return transactionReceipt;
        }

        public TransactionReceipt Execute(
            Transaction transaction,
            BlockHeader block)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            Address recipient = transaction.To;
            BigInteger value = transaction.Value;
            BigInteger gasPrice = transaction.GasPrice;
            long gasLimit = (long)transaction.GasLimit;
            byte[] machineCode = transaction.Init;
            byte[] data = transaction.Data ?? new byte[0];

            Address sender = _signer.RecoverAddress(transaction, block.Number);
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"SPEC: {spec.GetType().Name}");
                _logger.Debug("IS_CONTRACT_CREATION: " + transaction.IsContractCreation);
                _logger.Debug("IS_MESSAGE_CALL: " + transaction.IsMessageCall);
                _logger.Debug("IS_TRANSFER: " + transaction.IsTransfer);
                _logger.Debug("SENDER: " + sender);
                _logger.Debug("TO: " + transaction.To);
                _logger.Debug("GAS LIMIT: " + transaction.GasLimit);
                _logger.Debug("GAS PRICE: " + transaction.GasPrice);
                _logger.Debug("VALUE: " + transaction.Value);
                _logger.Debug("DATA_LENGTH: " + (transaction.Data?.Length ?? 0));
                _logger.Debug("NONCE: " + transaction.Nonce);
            }

            if (sender == null)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"SENDER_NOT_SPECIFIED");
                }

                return GetNullReceipt(block, 0L);
            }

            long intrinsicGas = _intrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("INTRINSIC GAS: " + intrinsicGas);
            }

            if (gasLimit < intrinsicGas)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                }

                return GetNullReceipt(block, 0L);
            }

            if (gasLimit > block.GasLimit - block.GasUsed)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GasLimit} - {block.GasUsed}");
                }

                return GetNullReceipt(block, 0L);
            }

            if (!_stateProvider.AccountExists(sender))
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"SENDER_ACCOUNT_DOES_NOT_EXIST {sender}");
                }

                _stateProvider.CreateAccount(sender, 0);
            }

            BigInteger senderBalance = _stateProvider.GetBalance(sender);
            if (intrinsicGas * gasPrice + value > senderBalance)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"INSUFFICIENT_SENDER_BALANCE: ({sender})b = {senderBalance}");
                }

                return GetNullReceipt(block, 0L);
            }

            if (transaction.Nonce != _stateProvider.GetNonce(sender))
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"WRONG_TRANSACTION_NONCE: {transaction.Nonce} (expected {_stateProvider.GetNonce(sender)})");
                }

                return GetNullReceipt(block, 0L);
            }

            _stateProvider.IncrementNonce(sender);
            _stateProvider.UpdateBalance(sender, -new BigInteger(gasLimit) * gasPrice, spec);
            _stateProvider.Commit(spec);

            long unspentGas = gasLimit - intrinsicGas;
            long spentGas = gasLimit;
            List<LogEntry> logEntries = new List<LogEntry>();

            if (transaction.IsContractCreation)
            {
                Rlp addressBaseRlp = Rlp.Encode(
                    Rlp.Encode(sender),
                    Rlp.Encode(_stateProvider.GetNonce(sender) - 1));
                Keccak addressBaseKeccak = Keccak.Compute(addressBaseRlp);
                recipient = new Address(addressBaseKeccak);
            }

            int snapshot = _stateProvider.TakeSnapshot();
            int storageSnapshot = _storageProvider.TakeSnapshot();
            _stateProvider.UpdateBalance(sender, -value, spec);
            byte statusCode = StatusCode.Failure;

            HashSet<Address> destroyedAccounts = new HashSet<Address>();
            try
            {
                if (transaction.IsContractCreation)
                {
                    if (_stateProvider.AccountExists(recipient) && !_stateProvider.IsEmptyAccount(recipient))
                    {
                        // TODO: review
                        throw new TransactionCollisionException();
                    }
                }

                if (transaction.IsTransfer)
                {
                    _stateProvider.UpdateBalance(sender, -value, spec);
                    _stateProvider.UpdateBalance(recipient, value, spec);
                    statusCode = StatusCode.Success;
                }
                else
                {
                    bool isPrecompile = recipient.IsPrecompiled(spec);

                    ExecutionEnvironment env = new ExecutionEnvironment();
                    env.Value = value;
                    env.TransferValue = value;
                    env.Sender = sender;
                    env.ExecutingAccount = recipient;
                    env.CurrentBlock = block;
                    env.GasPrice = gasPrice;
                    env.InputData = data ?? new byte[0];
                    env.CodeInfo = isPrecompile ? new CodeInfo(recipient) : new CodeInfo(machineCode ?? _stateProvider.GetCode(recipient));
                    env.Originator = sender;

                    ExecutionType executionType = isPrecompile
                        ? ExecutionType.DirectPrecompile
                        : transaction.IsContractCreation
                            ? ExecutionType.DirectCreate
                            : ExecutionType.Transaction;
                    EvmState state = new EvmState(unspentGas, env, executionType, false);

                    (byte[] output, TransactionSubstate substate) = _virtualMachine.Run(state, spec);

                    unspentGas = state.GasAvailable;

                    if (substate.ShouldRevert)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug("REVERTING");
                        }

                        logEntries.Clear();
                        destroyedAccounts.Clear();
                        _stateProvider.Restore(snapshot);
                        _storageProvider.Restore(storageSnapshot);
                    }
                    else
                    {
                        if (transaction.IsContractCreation)
                        {
                            long codeDepositGasCost = output.Length * GasCostOf.CodeDeposit;
                            if (spec.IsEip170Enabled && output.Length > 0x6000)
                            {
                                codeDepositGasCost = long.MaxValue;
                            }

                            if (unspentGas < codeDepositGasCost && spec.IsEip2Enabled)
                            {
                                throw new OutOfGasException();
                            }

                            if (unspentGas >= codeDepositGasCost)
                            {
                                Keccak codeHash = _stateProvider.UpdateCode(output);
                                _stateProvider.UpdateCodeHash(recipient, codeHash, spec);
                                unspentGas -= codeDepositGasCost;
                            }
                        }

                        logEntries.AddRange(substate.Logs);
                        foreach (Address toBeDestroyed in substate.DestroyList)
                        {
                            destroyedAccounts.Add(toBeDestroyed);
                        }

                        statusCode = StatusCode.Success;
                    }

                    spentGas = Refund(gasLimit, unspentGas, substate, sender, gasPrice, spec);
                }
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"EVM EXCEPTION: {ex.GetType().Name}");
                }

                logEntries.Clear();
                destroyedAccounts.Clear();
                _stateProvider.Restore(snapshot);
                _storageProvider.Restore(storageSnapshot);
            }

            foreach (Address toBeDestroyed in destroyedAccounts)
            {
                _stateProvider.DeleteAccount(toBeDestroyed);
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("GAS SPENT: " + spentGas);
            }

            if (!destroyedAccounts.Contains(block.Beneficiary))
            {
                if (!_stateProvider.AccountExists(block.Beneficiary))
                {
                    _stateProvider.CreateAccount(block.Beneficiary, spentGas * gasPrice);
                }
                else
                {
                    _stateProvider.UpdateBalance(block.Beneficiary, spentGas * gasPrice, spec);
                }
            }

            _storageProvider.Commit(spec);
            _stateProvider.Commit(spec);

            block.GasUsed += spentGas;
            return BuildTransactionReceipt(statusCode, logEntries, block.GasUsed, recipient);
        }

        private long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender, BigInteger gasPrice, IReleaseSpec spec)
        {
            long spentGas = gasLimit - unspentGas;
            long refund = Math.Min(spentGas / 2L, substate.Refund + substate.DestroyList.Count * RefundOf.Destroy);
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("REFUNDING UNUSED GAS OF " + unspentGas + " AND REFUND OF " + refund);
            }

            _stateProvider.UpdateBalance(sender, (unspentGas + refund) * gasPrice, spec);
            spentGas -= refund;
            return spentGas;
        }

        private TransactionReceipt BuildTransactionReceipt(byte statusCode, List<LogEntry> logEntries, long gasUsedSoFar, Address recipient)
        {
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = logEntries.ToArray();
            transactionReceipt.Bloom = BuildBloom(logEntries);
            transactionReceipt.GasUsed = gasUsedSoFar;
            transactionReceipt.PostTransactionState = _stateProvider.StateRoot;
            transactionReceipt.StatusCode = statusCode;
            transactionReceipt.Recipient = recipient;
            return transactionReceipt;
        }

        public static Bloom BuildBloom(List<LogEntry> logEntries)
        {
            Bloom bloom = new Bloom();
            for (int entryIndex = 0; entryIndex < logEntries.Count; entryIndex++)
            {
                LogEntry logEntry = logEntries[entryIndex];
                byte[] addressBytes = logEntry.LoggersAddress.Hex;
                bloom.Set(addressBytes);
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    Keccak topic = logEntry.Topics[topicIndex];
                    bloom.Set(topic.Bytes);
                }
            }

            return bloom;
        }
    }
}