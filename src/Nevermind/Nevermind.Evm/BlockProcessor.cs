using System;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    // TODO: work in progress
    public class BlockProcessor
    {
        public static Keccak GetReceiptsRoot(TransactionReceipt[] receipts)
        {
            PatriciaTree receiptTree = new PatriciaTree(new InMemoryDb());
            for (int i = 0; i < receipts.Length; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i]);
                receiptTree.Set(Rlp.Encode(0).Bytes, receiptRlp);
            }

            return receiptTree.RootHash;
        }

        public static Keccak GetTransactionsRoot(Transaction[] transactions)
        {
            PatriciaTree tranTree = new PatriciaTree(new InMemoryDb());
            for (int i = 0; i < transactions.Length; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                tranTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            return tranTree.RootHash;
        }

        public static void ApplyMinerReward()
        {
            throw new NotImplementedException();
            //Account minerAccount = _stateProvider.GetAccount(header.Beneficiary);
            //minerAccount.Balance += 5.Ether();
            //_stateProvider.UpdateAccount(header.Beneficiary, minerAccount);
        }
    }
}