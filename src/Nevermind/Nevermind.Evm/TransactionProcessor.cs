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

            if (transaction.GasLimit < intrinsicGas)
            {
                return null;
            }

            if (!_stateProvider.AccountExists(sender))
            {
                return null;
            }

            if (intrinsicGas * transaction.GasPrice + transaction.Value > _stateProvider.GetBalance(sender))
            {
                return null;
            }

            if (transaction.Nonce != _stateProvider.GetNonce(sender))
            {
                return null;
            }

            // checkpoint
            _stateProvider.IncrementNonce(sender);
            _stateProvider.UpdateBalance(sender, -transaction.GasLimit * transaction.GasPrice);

            // TODO: fail if not enough? or just revert?

            ulong gasAvailable = (ulong)(transaction.GasLimit - intrinsicGas);
            BigInteger gasSpent = transaction.GasLimit;
            List<LogEntry> logEntries = new List<LogEntry>();

            StateSnapshot snapshot = _stateProvider.TakeSnapshot();
            StateSnapshot storageSnapshot = transaction.To != null ? _storageProvider.TakeSnapshot(transaction.To) : null;
            _stateProvider.UpdateBalance(sender, -transaction.Value);

            if (transaction.IsContractCreation)
            {
                // TODO: extract since it is used in VM as well
                Rlp addressBaseRlp = Rlp.Encode(sender, _stateProvider.GetNonce(sender) - 1);
                Keccak addressBaseKeccak = Keccak.Compute(addressBaseRlp);
                Address contractAddress = new Address(addressBaseKeccak);

                ulong codeDepositCost = GasCostOf.CodeDeposit * (ulong)transaction.Init.Length;
                if (gasAvailable > (_protocolSpecification.IsEip2Enabled ? GasCostOf.Create + codeDepositCost : GasCostOf.Create))
                {
                    throw new OutOfGasException();
                }

                gasAvailable -= GasCostOf.Create;
                _stateProvider.CreateAccount(contractAddress, transaction.Value);
                if (!(_protocolSpecification.IsEip2Enabled && gasAvailable < codeDepositCost))
                {
                    gasAvailable -= codeDepositCost;
                    _stateProvider.UpdateCode(transaction.Init);
                    _stateProvider.UpdateCodeHash(contractAddress, Keccak.Compute(transaction.Init));
                }

                _stateProvider.UpdateBalance(sender, gasAvailable * transaction.GasPrice); // refund unused
                _stateProvider.IncrementNonce(sender);
            }
            else
            {
                // make transfer
                if (!_stateProvider.AccountExists(transaction.To))
                {
                    gasAvailable -= GasCostOf.NewAccount;
                    _stateProvider.CreateAccount(transaction.To, transaction.Value);
                }
                else
                {
                    _stateProvider.UpdateBalance(transaction.To, transaction.Value);
                }

                if (transaction.IsMessageCall)
                {
                    ExecutionEnvironment env = new ExecutionEnvironment();
                    env.Value = transaction.Value;
                    env.Caller = sender;
                    env.CodeOwner = transaction.To;
                    env.CurrentBlock = block;
                    env.GasPrice = transaction.GasPrice;
                    env.InputData = transaction.Data;
                    env.MachineCode = _stateProvider.GetCode(transaction.To);
                    env.Originator = sender;

                    EvmState state = new EvmState(gasAvailable);
                    try
                    {
                        (byte[] _, TransactionSubstate substate) =
                            _virtualMachine.Run(env, state, new BlockhashProvider(), _stateProvider, _storageProvider, _protocolSpecification);
                        logEntries.AddRange(substate.Logs);

                        gasAvailable = state.GasAvailable;

                        // pre-final
                        gasSpent = transaction.GasLimit -
                                   gasAvailable; // TODO: does refund use intrinsic value to calculate cap?
                        BigInteger halfOfGasSpend = BigInteger.Divide(gasSpent, 2);
                        BigInteger refund = BigInteger.Min(halfOfGasSpend, substate.Refund);
                        BigInteger gasUnused = gasAvailable + refund;
                        Console.WriteLine("REFUNDING UNUSED GAS OF " + gasUnused + " AND REFUND OF " + refund);
                        _stateProvider.UpdateBalance(sender, gasUnused * transaction.GasPrice);

                        gasSpent -= refund;

                        // final
                        foreach (Address toBeDestroyed in substate.DestroyList)
                        {
                            _stateProvider.DeleteAccount(toBeDestroyed);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        _stateProvider.Restore(snapshot);
                        _storageProvider.Restore(transaction.To, storageSnapshot);
                    }
                }
            }

            Console.WriteLine("GAS SPENT: " + gasSpent);
            _stateProvider.UpdateBalance(block.Beneficiary, gasSpent * transaction.GasPrice);

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