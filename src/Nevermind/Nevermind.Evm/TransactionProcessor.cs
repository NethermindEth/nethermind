using System;
using System.Linq;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Validators;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class TransactionProcessor
    {
        private static readonly IntrinsicGasCalculator IntrinsicGasCalculator = new IntrinsicGasCalculator();
        private readonly StateTree _state;
        private VirtualMachine _machine = new VirtualMachine();

        public TransactionProcessor(StateTree state)
        {
            _state = state;
        }

        private Account Get(Address address)
        {
            throw new NotImplementedException();
        }

        public TransactionReceipt Execute(
            TransactionSubstate substate,
            Address sender,
            Transaction transaction,
            BlockHeader block,
            BigInteger blockGasUsedSoFar)
        {
            if (sender == null)
            {
                return null;
            }

            if (!TransactionValidator.IsValid(transaction))
            {
                return null;
            }

            BigInteger intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, block.Number);
            if (intrinsicGas > block.GasLimit - blockGasUsedSoFar)
            {
                return null;
            }

            if (transaction.GasLimit < intrinsicGas)
            {
                return null;
            }

            Account senderAccount = Get(sender);
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
            transaction.Nonce++;
            senderAccount.Balance -= transaction.GasLimit - intrinsicGas;
            BigInteger gasAvailable = transaction.GasLimit - intrinsicGas;

            if (transaction.IsContractCreation)
            {
                Rlp addressBaseRlp = Rlp.Encode(sender, senderAccount.Nonce - 1);
                Keccak addressBaseKeccak = Keccak.Compute(addressBaseRlp);
                Address contractAddress = new Address(addressBaseKeccak);

                senderAccount.Balance -= transaction.Value;

                Account contractAccount = new Account();
                contractAccount.Balance += transaction.Value;
                contractAccount.Nonce = 0;
                contractAccount.CodeHash = Keccak.OfAnEmptyString;
                contractAccount.StorageRoot = Keccak.OfAnEmptyString;

                // creation code
                // before homestead you may end up with value transfer but no code deployed

                Rlp contractAccountRlp = Rlp.Encode(contractAccount);
                _state.Set(contractAddress, contractAccountRlp);
            }
            else if (transaction.IsMessageCall)
            {
                senderAccount.Balance -= transaction.Value;

                Account recipientAccount = Get(transaction.To) ?? new Account();
                recipientAccount.Balance += transaction.Value;
            }

            // pre-final
            BigInteger gasSpent = transaction.GasLimit - gasAvailable;
            BigInteger halfOfGasSpend = BigInteger.Divide(gasSpent, 2);
            BigInteger refund = gasAvailable +
                                BigInteger.Min(halfOfGasSpend * transaction.GasPrice, substate.Refund);
            senderAccount.Balance += refund;

            // final
            Account minerAccount = Get(block.Beneficiary);
            minerAccount.Balance += gasSpent * transaction.GasPrice;
            foreach (Address toBeDestroyed in substate.DestroyList)
            {
                _state.Set(toBeDestroyed, null);
            }

            // receipt
            TransactionReceipt receipt = new TransactionReceipt();
            receipt.Logs = substate.Logs.ToArray(); // is it held between transactions?
            receipt.Bloom = new Bloom(); // calculate from logs?
            receipt.GasUsed = blockGasUsedSoFar + gasSpent;
            receipt.PostTransactionState = _state.RootHash;

            return receipt;
        }
    }
}