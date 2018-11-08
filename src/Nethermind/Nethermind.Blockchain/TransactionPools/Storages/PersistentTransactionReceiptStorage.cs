using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Store;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class PersistentTransactionReceiptStorage : ITransactionReceiptStorage
    {
        private readonly IDb _receiptsDb;
        private readonly ISpecProvider _specProvider;

        public PersistentTransactionReceiptStorage(IDb receiptsDb, ISpecProvider specProvider)
        {
            _receiptsDb = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public TransactionReceipt Get(Keccak hash)
        {
            var receiptData = _receiptsDb.Get(hash);

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
            _receiptsDb.Set(receipt.TransactionHash,
                Rlp.Encode(receipt, spec.IsEip658Enabled
                    ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage
                    : RlpBehaviors.Storage).Bytes);
        }
    }
}