using System;
using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class TransactionProcessor : ITransactionProcessor
    {
        private static readonly IntrinsicGasCalculator IntrinsicGasCalculator = new IntrinsicGasCalculator();
        private readonly ILogger _logger;
        private readonly IProtocolSpecification _protocolSpecification;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly IVirtualMachine _virtualMachine;

        public TransactionProcessor(IProtocolSpecification protocolSpecification, IStateProvider stateProvider, IStorageProvider storageProvider, IVirtualMachine virtualMachine, ChainId chainId, ILogger logger)
        {
            _virtualMachine = virtualMachine;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _protocolSpecification = protocolSpecification;
            _logger = logger;
            ChainId = chainId;
        }

        public ChainId ChainId { get; }

        private TransactionReceipt GetNullReceipt(long totalGasUsed)
        {
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = new LogEntry[0];
            transactionReceipt.Bloom = new Bloom();
            transactionReceipt.GasUsed = totalGasUsed;
            transactionReceipt.PostTransactionState = _stateProvider.StateRoot;
            transactionReceipt.StatusCode = StatusCode.Failure;
            return transactionReceipt;
        }

        public TransactionReceipt Execute(
            Transaction transaction,
            BlockHeader block)
        {
            Address recipient = transaction.To;
            BigInteger value = transaction.Value;
            BigInteger gasPrice = transaction.GasPrice;
            long gasLimit = (long)transaction.GasLimit;
            byte[] machineCode = transaction.Init;
            byte[] data = transaction.Data ?? new byte[0];

            Address sender = Signer.Recover(transaction);
            _logger?.Log("IS_CONTRACT_CREATION: " + transaction.IsContractCreation);
            _logger?.Log("IS_MESSAGE_CALL: " + transaction.IsMessageCall);
            _logger?.Log("IS_TRANSFER: " + transaction.IsTransfer);
            _logger?.Log("SENDER: " + sender);
            _logger?.Log("TO: " + transaction.To);
            _logger?.Log("GAS LIMIT: " + transaction.GasLimit);
            _logger?.Log("GAS PRICE: " + transaction.GasPrice);
            _logger?.Log("VALUE: " + transaction.Value);
            _logger?.Log("DATA_LENGTH: " + (transaction.Data?.Length ?? 0));

            if (sender == null)
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            long intrinsicGas = IntrinsicGasCalculator.Calculate(_protocolSpecification, transaction);
            _logger?.Log("INTRINSIC GAS: " + intrinsicGas);

            if (gasLimit < intrinsicGas)
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            if (gasLimit > block.GasLimit - block.GasUsed)
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            BigInteger senderBalance = _stateProvider.GetBalance(sender);
            _logger?.Log($"SENDER_BALANCE: {senderBalance}");
            if (intrinsicGas * gasPrice + value > senderBalance)
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            if (transaction.Nonce != _stateProvider.GetNonce(sender))
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }
            
            if (!_stateProvider.AccountExists(sender))
            {
                _stateProvider.CreateAccount(sender, 0);
            }

            _stateProvider.IncrementNonce(sender);
            _stateProvider.UpdateBalance(sender, -new BigInteger(gasLimit) * gasPrice);
            _stateProvider.Commit(); // TODO: can remove this commit

            long unspentGas = gasLimit - intrinsicGas;
            long spentGas = gasLimit;
            List<LogEntry> logEntries = new List<LogEntry>();

            if (transaction.IsContractCreation)
            {
                Rlp addressBaseRlp = Rlp.Encode(sender, _stateProvider.GetNonce(sender) - 1);
                Keccak addressBaseKeccak = Keccak.Compute(addressBaseRlp);
                recipient = new Address(addressBaseKeccak);
            }

            int snapshot = _stateProvider.TakeSnapshot();
            int storageSnapshot = _storageProvider.TakeSnapshot();
            _stateProvider.UpdateBalance(sender, -value);
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
                    _stateProvider.UpdateBalance(sender, -value);
                    _stateProvider.UpdateBalance(recipient, value);
                    statusCode = StatusCode.Success;
                }
                else
                {
                    bool isPrecompile = recipient.IsPrecompiled(_protocolSpecification);

                    ExecutionEnvironment env = new ExecutionEnvironment();
                    env.Value = value;
                    env.TransferValue = value;
                    env.Sender = sender;
                    env.ExecutingAccount = recipient;
                    env.CurrentBlock = block;
                    env.GasPrice = gasPrice;
                    env.InputData = data ?? new byte[0];
                    env.MachineCode = isPrecompile ? (byte[])recipient.Hex : machineCode ?? _stateProvider.GetCode(recipient);
                    env.Originator = sender;

                    ExecutionType executionType = isPrecompile
                        ? ExecutionType.DirectPrecompile
                        : transaction.IsContractCreation
                            ? ExecutionType.DirectCreate
                            : ExecutionType.Transaction;
                    EvmState state = new EvmState(unspentGas, env, executionType, false);

                    (byte[] output, TransactionSubstate substate) = _virtualMachine.Run(state);

                    unspentGas = state.GasAvailable;

                    if (substate.ShouldRevert)
                    {
                        _logger?.Log("REVERTING");

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
                            if (_protocolSpecification.IsEip170Enabled && output.Length > 0x6000)
                            {
                                codeDepositGasCost = long.MaxValue;
                            }

                            if (unspentGas < codeDepositGasCost && _protocolSpecification.IsEip2Enabled)
                            {
                                throw new OutOfGasException();
                            }

                            if (unspentGas >= codeDepositGasCost)
                            {
                                Keccak codeHash = _stateProvider.UpdateCode(output);
                                _stateProvider.UpdateCodeHash(recipient, codeHash);
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

                    spentGas = Refund(gasLimit, unspentGas, substate, sender, gasPrice);
                }
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException)
            {
                _logger?.Log($"EVM EXCEPTION: {ex.GetType().Name}");

                logEntries.Clear();
                destroyedAccounts.Clear();
                _stateProvider.Restore(snapshot);
                _storageProvider.Restore(storageSnapshot);
            }

            foreach (Address toBeDestroyed in destroyedAccounts)
            {
                _stateProvider.DeleteAccount(toBeDestroyed);
            }

            _logger?.Log("GAS SPENT: " + spentGas);
            if (!destroyedAccounts.Contains(block.Beneficiary))
            {
                if (!_stateProvider.AccountExists(block.Beneficiary))
                {
                    _stateProvider.CreateAccount(block.Beneficiary, spentGas * gasPrice);
                }
                else
                {
                    _stateProvider.UpdateBalance(block.Beneficiary, spentGas * gasPrice);
                }
            }

            _storageProvider.Commit();
            _stateProvider.Commit();

            block.GasUsed += spentGas;
            return BuildTransactionReceipt(statusCode, logEntries, block.GasUsed);
        }

        private long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender, BigInteger gasPrice)
        {
            long spentGas = gasLimit - unspentGas;
            long refund = Math.Min(spentGas / 2L, substate.Refund + substate.DestroyList.Count * RefundOf.Destroy);
            _logger?.Log("REFUNDING UNUSED GAS OF " + unspentGas + " AND REFUND OF " + refund);
            _stateProvider.UpdateBalance(sender, (unspentGas + refund) * gasPrice);
            spentGas -= refund;
            return spentGas;
        }

        private TransactionReceipt BuildTransactionReceipt(byte statusCode, List<LogEntry> logEntries, long gasUsedSoFar)
        {
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = logEntries.ToArray();
            transactionReceipt.Bloom = BuildBloom(logEntries);
            transactionReceipt.GasUsed = gasUsedSoFar;
            transactionReceipt.PostTransactionState = _stateProvider.StateRoot;
            transactionReceipt.StatusCode = statusCode;
            return transactionReceipt;
        }

        private static Bloom BuildBloom(List<LogEntry> logEntries)
        {
            Bloom bloom = new Bloom();
            foreach (LogEntry logEntry in logEntries)
            {
                byte[] addressBytes = logEntry.LoggersAddress.Hex;
                bloom.Set(addressBytes);
                foreach (Keccak topic in logEntry.Topics)
                {
                    bloom.Set(topic.Bytes);
                }
            }

            return bloom;
        }
    }
}