using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class PersistentTransactionStorage : ITransactionStorage
    {
        private readonly IDb _database;
        private readonly ISpecProvider _specProvider;

        public PersistentTransactionStorage(IDb database, ISpecProvider specProvider)
        {
            _database = database;
            _specProvider = specProvider;
        }

        public Transaction Get(Keccak hash)
        {
            var transactionData = _database.Get(hash);

            return transactionData == null
                ? null
                : Rlp.Decode<Transaction>(new Rlp(transactionData), RlpBehaviors.Storage);
        }

        public Transaction[] GetAll()
        {
            throw new System.NotImplementedException();
        }

        public void Add(Transaction transaction, UInt256 blockNumber)
        {
            if (transaction == null)
            {
                throw new ArgumentNullException(nameof(transaction));
            }

            var spec = _specProvider.GetSpec(blockNumber);
            _database.Set(transaction.Hash,
                Rlp.Encode(transaction, spec.IsEip658Enabled
                    ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage
                    : RlpBehaviors.Storage).Bytes);
        }

        public void Delete(Keccak hash)
        {
            throw new System.NotImplementedException();
        }
    }
}