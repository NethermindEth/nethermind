using System;
using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Validators;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class TransactionProcessor
    {
        private readonly IWorldStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly IProtocolSpecification _protocolSpecification;
        private readonly IVirtualMachine _virtualMachine;
        public ChainId ChainId { get; }

        private static readonly IntrinsicGasCalculator IntrinsicGasCalculator = new IntrinsicGasCalculator();

        public TransactionProcessor(
            IVirtualMachine virtualMachine,
            IWorldStateProvider stateProvider,
            IStorageProvider storageProvider,
            IProtocolSpecification protocolSpecification,
            ChainId chainId)
        {
            _virtualMachine = virtualMachine;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _protocolSpecification = protocolSpecification;
            ChainId = chainId;
        }

        public TransactionReceipt Execute(
            Address sender,
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

            Console.WriteLine("GAS LIMIT: " + transaction.GasLimit);
            Console.WriteLine("GAS PRICE: " + transaction.GasPrice);
            Console.WriteLine("VALUE: " + transaction.Value);

            if (sender == null)
            {
                return null;
            }

            if (!TransactionValidator.IsValid(transaction, sender, _protocolSpecification.IsEip155Enabled, (int)ChainId))
            {
                return null;
            }

            ulong intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, block.Number);
            Console.WriteLine("INTRINSIC GAS: " + intrinsicGas);
            // THEIR TOTAL: 29198 - on state
            // THEIR IMPLIED INTRINSIC: 29158

            // MY TOTAL: 28704
            // MY INTRINSIC: 28664
            // MY VM: 40

            if (intrinsicGas > block.GasLimit - blockGasUsedSoFar)
            {
                return null;
            }

            if (gasLimit < intrinsicGas)
            {
                return null;
            }

            if (!_stateProvider.AccountExists(sender))
            {
                return null;
            }

            if (intrinsicGas * gasPrice + value > _stateProvider.GetBalance(sender))
            {
                return null;
            }

            if (transaction.Nonce != _stateProvider.GetNonce(sender))
            {
                return null;
            }

            // checkpoint
            _stateProvider.IncrementNonce(sender);
            _stateProvider.UpdateBalance(sender, -gasLimit * gasPrice);

            // TODO: fail if not enough? or just revert?

            ulong gasAvailable = (ulong)(gasLimit - intrinsicGas);
            BigInteger gasSpent = gasLimit;
            List<LogEntry> logEntries = new List<LogEntry>();

            StateSnapshot snapshot = _stateProvider.TakeSnapshot();
            StateSnapshot storageSnapshot = recipient != null ? _storageProvider.TakeSnapshot(recipient) : null;
            _stateProvider.UpdateBalance(sender, -value);

            try
            {
                if (transaction.IsContractCreation)
                {
                    if (ShouldLog.Evm)
                    {
                        Console.WriteLine("THIS IS CONTRACT CREATION");
                    }
                    // TODO: extract since it is used in VM as well
                    Rlp addressBaseRlp = Rlp.Encode(sender, _stateProvider.GetNonce(sender) - 1);
                    Keccak addressBaseKeccak = Keccak.Compute(addressBaseRlp);
                    recipient = new Address(addressBaseKeccak);

                    ulong codeDepositCost = GasCostOf.CodeDeposit * (ulong)data.Length;
                    if (gasAvailable < (_protocolSpecification.IsEip2Enabled ? GasCostOf.Create + codeDepositCost : codeDepositCost))
                    {
                        throw new OutOfGasException();
                    }

                    if (_stateProvider.AccountExists(recipient) && !_stateProvider.IsEmptyAccount(recipient))
                    {
                        throw new TransactionCollissionException();
                    }

                    if (!_stateProvider.AccountExists(recipient))
                    {
                        _stateProvider.CreateAccount(recipient, value);
                    }
                    else
                    {
                        _stateProvider.UpdateBalance(recipient, value);
                    }

                    if (_protocolSpecification.IsEip2Enabled)
                    {
                        gasAvailable -= GasCostOf.Create;
                    }

                    if (gasAvailable >= codeDepositCost)
                    {
                        gasAvailable -= codeDepositCost;
                        _stateProvider.UpdateCode(data);
                        _stateProvider.UpdateCodeHash(recipient, Keccak.Compute(data));
                    }
                }
                else
                {
                    if (!_stateProvider.AccountExists(recipient))
                    {
                        if (value != BigInteger.Zero)
                        {
                            gasAvailable -= GasCostOf.NewAccount;
                        }

                        _stateProvider.CreateAccount(recipient, value);
                    }
                    else
                    {
                        _stateProvider.UpdateBalance(recipient, value);
                    }
                }

                if (!transaction.IsTransfer)
                {
                    ExecutionEnvironment env = new ExecutionEnvironment();
                    env.Value = value;
                    env.Caller = sender;
                    env.CodeOwner = recipient;
                    env.CurrentBlock = block;
                    env.GasPrice = gasPrice;
                    env.InputData = transaction.Data ?? new byte[0];
                    env.MachineCode = machineCode ?? _stateProvider.GetCode(recipient);
                    env.Originator = sender;

                    EvmState state = new EvmState(gasAvailable);

                    if (_protocolSpecification.IsEip170Enabled
                        && transaction.IsContractCreation
                        && env.MachineCode.Length > 0x6000)
                    {
                        throw new OutOfGasException();
                    }

                    (byte[] _, TransactionSubstate substate) =
                        _virtualMachine.Run(env, state, new BlockhashProvider(), _stateProvider, _storageProvider, _protocolSpecification);
                    logEntries.AddRange(substate.Logs);

                    gasAvailable = state.GasAvailable;

                    // pre-final
                    gasSpent = gasLimit - gasAvailable; // TODO: does refund use intrinsic value to calculate cap?
                    BigInteger halfOfGasSpend = BigInteger.Divide(gasSpent, 2);
                    BigInteger refund = BigInteger.Min(halfOfGasSpend, substate.Refund);
                    BigInteger gasUnused = gasAvailable + refund;
                    Console.WriteLine("REFUNDING UNUSED GAS OF " + gasUnused + " AND REFUND OF " + refund);
                    _stateProvider.UpdateBalance(sender, gasUnused * gasPrice);

                    gasSpent -= refund;

                    // final
                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        _stateProvider.DeleteAccount(toBeDestroyed);
                    }

                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"  EVM EXCEPTION: {e.GetType().Name}");
                _stateProvider.Restore(snapshot);
                _storageProvider.Restore(recipient, storageSnapshot);
            }

            Console.WriteLine("GAS SPENT: " + gasSpent);
            if (!_stateProvider.AccountExists(block.Beneficiary))
            {
                _stateProvider.CreateAccount(block.Beneficiary, gasSpent * gasPrice);
            }
            else
            {
                _stateProvider.UpdateBalance(block.Beneficiary, gasSpent * gasPrice);
            }

            TransactionReceipt transferReceipt = new TransactionReceipt();
            transferReceipt.Logs = logEntries.ToArray();
            transferReceipt.Bloom = new Bloom();
            foreach (LogEntry logEntry in logEntries)
            {
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