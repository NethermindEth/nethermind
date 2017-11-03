using System;
using System.Linq;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Core.Validators;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class TransactionProcessor
    {
        private readonly IWorldStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly IVirtualMachine _virtualMachine;
        public ChainId ChainId { get; }
        public bool UsesEip155Rule { get; }
        
        private static readonly IntrinsicGasCalculator IntrinsicGasCalculator = new IntrinsicGasCalculator();

        public TransactionProcessor(IVirtualMachine virtualMachine, IWorldStateProvider stateProvider, IStorageProvider storageProvider, ChainId chainId, bool usesEip155Rule = false)
        {
            _virtualMachine = virtualMachine;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            ChainId = chainId;
            UsesEip155Rule = usesEip155Rule;
        }

        public TransactionReceipt Execute(            
            Address sender,
            Transaction transaction,
            BlockHeader block,
            BigInteger blockGasUsedSoFar)
        {
            if (sender == null)
            {
                return null;
            }

            if (!TransactionValidator.IsValid(transaction, sender, UsesEip155Rule, (int)ChainId))
            {
                return null;
            }

            ulong intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, block.Number);
            if (intrinsicGas > block.GasLimit - blockGasUsedSoFar)
            {
                return null;
            }

            if (transaction.GasLimit < intrinsicGas)
            {
                return null;
            }

            Account senderAccount = _stateProvider.GetAccount(sender);
            if (senderAccount == null)
            {
                return null;
            }

            if (intrinsicGas * transaction.GasPrice + transaction.Value > senderAccount.Balance)
            {
                return null;
            }

            if (transaction.Nonce != senderAccount.Nonce)
            {
                return null;
            }

            // checkpoint
            senderAccount.Nonce++;
            senderAccount.Balance -= transaction.GasLimit * transaction.GasPrice;
            senderAccount.Balance -= transaction.Value;
            _stateProvider.UpdateAccount(sender, senderAccount);
            // TODO: fail if not enough? or just revert?

            ulong gasAvailable = (ulong)(transaction.GasLimit - intrinsicGas);

            if (transaction.IsContractCreation)
            {
                // TODO: extract since it is used in VM as well
                Rlp addressBaseRlp = Rlp.Encode(sender, senderAccount.Nonce - 1);
                Keccak addressBaseKeccak = Keccak.Compute(addressBaseRlp);
                Address contractAddress = new Address(addressBaseKeccak);
                Account contractAccount = new Account();
                contractAccount.Balance += transaction.Value;
                contractAccount.Nonce = 0;
                contractAccount.CodeHash = Keccak.OfAnEmptyString;
                contractAccount.StorageRoot = Keccak.OfAnEmptyString;

                // creation code
                // before homestead you may end up with value transfer but no code deployed

                gasAvailable -= GasCostOf.Create;
                gasAvailable -= GasCostOf.CodeDeposit * (ulong)transaction.Init.Length;

                senderAccount.Balance += gasAvailable; // refund unused
                _stateProvider.UpdateAccount(contractAddress, contractAccount);

                TransactionReceipt receipt = new TransactionReceipt();
                receipt.Logs = new LogEntry[0];
                receipt.Bloom = new Bloom();
                receipt.GasUsed = transaction.GasLimit - gasAvailable;
                receipt.PostTransactionState = _stateProvider.State.RootHash;
                return receipt;
            }

            // make transfer
            Account recipientAccount = _stateProvider.GetAccount(transaction.To);
            if (recipientAccount == null)
            {
                gasAvailable -= GasCostOf.NewAccount;
                recipientAccount = new Account();
            }

            recipientAccount.Balance += transaction.Value;
            _stateProvider.UpdateAccount(transaction.To, recipientAccount);

            if (transaction.IsMessageCall)
            {
                ExecutionEnvironment env = new ExecutionEnvironment();
                env.Value = transaction.Value;
                env.Caller = sender;
                env.CodeOwner = transaction.To;
                env.CurrentBlock = block;
                env.GasPrice = transaction.GasPrice;
                env.InputData = transaction.Data;
                env.MachineCode = _stateProvider.GetCode(recipientAccount.CodeHash);

                EvmState state = new EvmState(gasAvailable);
                StateSnapshot snapshot = _stateProvider.TakeSnapshot();
                StateSnapshot storageSnapshot = _storageProvider.TakeSnapshot(transaction.To);
                try
                {
                    (byte[] output, TransactionSubstate substate) =
                        _virtualMachine.Run(env, state, new BlockhashProvider(), _stateProvider, _storageProvider);

                    gasAvailable = state.GasAvailable;

                    // pre-final
                    BigInteger gasSpent = transaction.GasLimit - gasAvailable; // TODO: does refund use intrinsic value to calculate cap?
                    BigInteger halfOfGasSpend = BigInteger.Divide(gasSpent, 2);
                    BigInteger refund = gasAvailable + BigInteger.Min(halfOfGasSpend, substate.Refund);
                    senderAccount.Balance += refund * transaction.GasPrice;
                    _stateProvider.UpdateAccount(sender, senderAccount);

                    // final

                    Account minerAccount = _stateProvider.GetOrCreateAccount(block.Beneficiary); // not sure about account creation here
                    minerAccount.Balance += gasSpent * transaction.GasPrice;
                    _stateProvider.UpdateAccount(block.Beneficiary, minerAccount);
                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        _stateProvider.UpdateAccount(toBeDestroyed, null);
                    }

                    minerAccount.Balance += 5.Ether();
                    _stateProvider.UpdateAccount(block.Beneficiary, minerAccount);

                    // receipt
                    TransactionReceipt receipt = new TransactionReceipt();
                    receipt.Logs = substate.Logs.ToArray(); // is it held between transactions?
                    receipt.Bloom = new Bloom(); // calculate from logs?
                    receipt.GasUsed = blockGasUsedSoFar + gasSpent;
                    receipt.PostTransactionState = _stateProvider.State.RootHash;
                    return receipt;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    _stateProvider.Restore(snapshot);
                    _storageProvider.Restore(transaction.To, storageSnapshot);

                    TransactionReceipt receipt = new TransactionReceipt();
                    receipt.Logs = new LogEntry[0];
                    receipt.Bloom = new Bloom();
                    receipt.GasUsed = transaction.GasLimit;
                    receipt.PostTransactionState = _stateProvider.State.RootHash;
                    return receipt;
                }
            }

            TransactionReceipt transferReceipt = new TransactionReceipt();
            transferReceipt.Logs = new LogEntry[0];
            transferReceipt.Bloom = new Bloom();
            transferReceipt.GasUsed = transaction.GasLimit;
            transferReceipt.PostTransactionState = _stateProvider.State.RootHash;
            return transferReceipt;
        }
    }
}