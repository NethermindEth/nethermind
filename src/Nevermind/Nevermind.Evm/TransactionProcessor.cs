using System;
using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Signing;
using Nevermind.Core.Validators;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class TransactionProcessor
    {
        private static readonly IntrinsicGasCalculator IntrinsicGasCalculator = new IntrinsicGasCalculator();
        private readonly ILogger _logger;
        private readonly IProtocolSpecification _protocolSpecification;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly IVirtualMachine _virtualMachine;

        public TransactionProcessor(
            IVirtualMachine virtualMachine,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            IProtocolSpecification protocolSpecification,
            ChainId chainId,
            ILogger logger)
        {
            _virtualMachine = virtualMachine;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _protocolSpecification = protocolSpecification;
            _logger = logger;
            ChainId = chainId;
        }

        public ChainId ChainId { get; }

        private TransactionReceipt GetNullReceipt(BigInteger totalGasUsed)
        {
            TransactionReceipt transferReceipt = new TransactionReceipt();
            transferReceipt.Logs = new LogEntry[0];
            transferReceipt.Bloom = new Bloom();
            transferReceipt.GasUsed = totalGasUsed;
            transferReceipt.PostTransactionState = _stateProvider.State.RootHash;
            return transferReceipt;
        }

        public TransactionReceipt Execute(
            Transaction transaction,
            BlockHeader block,
            BigInteger blockGasUsedSoFar)
        {
            Address recipient = transaction.To;
            BigInteger value = transaction.Value;
            BigInteger gasPrice = transaction.GasPrice;
            BigInteger gasLimit = transaction.GasLimit;
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

            if (!TransactionValidator.IsValid(transaction, sender, _protocolSpecification.IsEip155Enabled, (int)ChainId))
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            ulong intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, block.Number);
            _logger?.Log("INTRINSIC GAS: " + intrinsicGas);

            if (intrinsicGas > block.GasLimit - blockGasUsedSoFar)
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            if (gasLimit < intrinsicGas)
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            if (!_stateProvider.AccountExists(sender))
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            if (intrinsicGas * gasPrice + value > _stateProvider.GetBalance(sender))
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            if (transaction.Nonce != _stateProvider.GetNonce(sender))
            {
                return GetNullReceipt(block.GasUsed + gasLimit);
            }

            _stateProvider.IncrementNonce(sender);
            _stateProvider.UpdateBalance(sender, -gasLimit * gasPrice); // TODO: fail if not enough? or just revert?

            ulong gasAvailable = (ulong)(gasLimit - intrinsicGas);
            BigInteger gasSpent = gasLimit;
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

            HashSet<Address> destroyedAccounts = new HashSet<Address>();
            // TODO: can probably merge it with the inner loop in VM
            try
            {
                if (transaction.IsContractCreation)
                {
                    _logger?.Log("THIS IS CONTRACT CREATION");

                    if (_stateProvider.AccountExists(recipient) && !_stateProvider.IsEmptyAccount(recipient))
                    {
                        //BigInteger balance = _stateProvider.GetBalance(recipient);
                        //_stateProvider.CreateAccount(recipient, balance);
                        throw new TransactionCollisionException();
                    }

                    if (_protocolSpecification.IsEip2Enabled)
                    {
                        if (gasAvailable < GasCostOf.Create)
                        {
                            throw new OutOfGasException();
                        }

                        gasAvailable -= GasCostOf.Create;
                    }
                }

                if (transaction.IsTransfer)
                {
                    _stateProvider.UpdateBalance(sender, -value);
                    _stateProvider.UpdateBalance(recipient, value);
                }
                else
                {
                    bool isPrecompile = recipient.IsPrecompiled();

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

                    EvmState state = new EvmState(gasAvailable, env, isPrecompile ? ExecutionType.DirectPrecompile : ExecutionType.Transaction, false);

                    if (_protocolSpecification.IsEip170Enabled
                        && transaction.IsContractCreation
                        && env.MachineCode.Length > 0x6000)
                    {
                        throw new OutOfGasException();
                    }

                    (byte[] output, TransactionSubstate substate) = _virtualMachine.Run(state);
                    logEntries.AddRange(substate.Logs);

                    gasAvailable = state.GasAvailable;

                    if (transaction.IsContractCreation)
                    {
                        ulong codeDepositGasCost = GasCostOf.CodeDeposit * (ulong)output.Length;
                        if (gasAvailable < codeDepositGasCost && _protocolSpecification.IsEmptyCodeContractBugFixed)
                        {
                            throw new OutOfGasException();
                        }

                        if (gasAvailable >= codeDepositGasCost)
                        {
                            Keccak codeHash = _stateProvider.UpdateCode(output);
                            _stateProvider.UpdateCodeHash(recipient, codeHash);
                            gasAvailable -= codeDepositGasCost;
                        }
                    }

                    // pre-final
                    gasSpent = gasLimit - gasAvailable; // TODO: does refund use intrinsic value to calculate cap?
                    BigInteger halfOfGasSpend = BigInteger.Divide(gasSpent, 2);

                    ulong destroyRefund = (ulong)substate.DestroyList.Count * RefundOf.Destroy;
                    BigInteger refund = BigInteger.Min(halfOfGasSpend, substate.Refund + destroyRefund);
                    BigInteger gasUnused = gasAvailable + refund;
                    _logger?.Log("REFUNDING UNUSED GAS OF " + gasUnused + " AND REFUND OF " + refund);
                    _stateProvider.UpdateBalance(sender, gasUnused * gasPrice);

                    gasSpent -= refund;

                    // final
                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        _stateProvider.DeleteAccount(toBeDestroyed);
                        destroyedAccounts.Add(toBeDestroyed);
                    }
                }
            }
            catch (EvmException e)
            {
                _logger?.Log($"  EVM EXCEPTION: {e.GetType().Name}");
                logEntries.Clear();
                destroyedAccounts.Clear();
                _stateProvider.Restore(snapshot);
                _storageProvider.Restore(storageSnapshot);

                _logger?.Log("GAS SPENT: " + gasSpent);
            }

            if (!destroyedAccounts.Contains(block.Beneficiary))
            {
                if (!_stateProvider.AccountExists(block.Beneficiary))
                {
                    _stateProvider.CreateAccount(block.Beneficiary, gasSpent * gasPrice);
                }
                else
                {
                    _stateProvider.UpdateBalance(block.Beneficiary, gasSpent * gasPrice);
                }
            }

            _storageProvider.Commit(_stateProvider);
            _stateProvider.Commit();

            TransactionReceipt transferReceipt = new TransactionReceipt();
            transferReceipt.Logs = logEntries.ToArray();
            transferReceipt.Bloom = new Bloom();
            foreach (LogEntry logEntry in logEntries)
            {
                byte[] addressBytes = logEntry.LoggersAddress.Hex;
                transferReceipt.Bloom.Set(addressBytes);
                foreach (Keccak entryTopic in logEntry.Topics)
                {
                    transferReceipt.Bloom.Set(entryTopic.Bytes);
                }
            }

            transferReceipt.GasUsed = block.GasUsed + gasSpent;
            transferReceipt.PostTransactionState = _stateProvider.State.RootHash;
            return transferReceipt;
        }
    }
}