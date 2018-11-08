using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Store;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class PersistentReceiptStorage : IReceiptStorage
    {
        private readonly IDb _database;
        private readonly ISpecProvider _specProvider;

        public PersistentReceiptStorage(IDb database, ISpecProvider specProvider)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public TransactionReceipt Get(Keccak hash)
        {
            var receiptData = _database.Get(hash);

            return receiptData == null
                ? null
                : Rlp.Decode<TransactionReceipt>(new Rlp(receiptData), RlpBehaviors.Storage);
        }

        public void Add(TransactionReceipt receipt)
        {
            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            var spec = _specProvider.GetSpec(receipt.BlockNumber);
            _database.Set(receipt.TransactionHash,
                Rlp.Encode(receipt, spec.IsEip658Enabled
                    ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage
                    : RlpBehaviors.Storage).Bytes);
        }
    }
}