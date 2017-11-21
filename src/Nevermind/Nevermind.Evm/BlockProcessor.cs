using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Store;

namespace Nevermind.Evm
{
    // TODO: work in progress
    public class BlockProcessor
    {
        private readonly IProtocolSpecification _protocolSpecification;
        private readonly IStateProvider _stateProvider;

        public BlockProcessor(IProtocolSpecification protocolSpecification, IStateProvider stateProvider)
        {
            _protocolSpecification = protocolSpecification;
            _stateProvider = stateProvider;
        }

        public Keccak GetReceiptsRoot(TransactionReceipt[] receipts)
        {
            PatriciaTree receiptTree = new PatriciaTree(new InMemoryDb());
            for (int i = 0; i < receipts.Length; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _protocolSpecification.IsEip658Enabled);
                receiptTree.Set(Rlp.Encode(0).Bytes, receiptRlp);
            }

            return receiptTree.RootHash;
        }

        public Keccak GetTransactionsRoot(Transaction[] transactions)
        {
            PatriciaTree tranTree = new PatriciaTree(new InMemoryDb());
            for (int i = 0; i < transactions.Length; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                tranTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            return tranTree.RootHash;
        }

        public void ApplyMinerReward(Address beneficiary)
        {
            BigInteger reward = _protocolSpecification.IsEip186Enabled ? 3.Ether() : 5.Ether();
            if (!_stateProvider.AccountExists(beneficiary))
            {
                _stateProvider.CreateAccount(beneficiary, reward);
            }
            else
            {
                _stateProvider.UpdateBalance(beneficiary, reward);
            }

            _stateProvider.Commit();
        }
    }
}