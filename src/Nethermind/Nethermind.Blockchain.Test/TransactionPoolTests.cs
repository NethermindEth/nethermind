using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.TransactionPools.Filters;
using Nethermind.Blockchain.TransactionPools.Storages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class TransactionPoolTests
    {
        private IEthereumSigner _ethereumSigner;
        private ISpecProvider _specProvider;
        private ILogManager _logManager;
        private Block _genesisBlock;
        private ITransactionPool _transactionPool;
        private ITransactionStorage _storage;
        private ITransactionReceiptStorage _receiptStorage;
        private IBlockTree _remoteBlockTree;

        [SetUp]
        public void Setup()
        {
            _logManager = NullLogManager.Instance;
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumSigner = new EthereumSigner(_specProvider, _logManager);
            _storage = new NoTransactionStorage();
            _receiptStorage = new NoTransactionReceiptStorage();
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
        }

        [Test]
        public void Test()
        {
            _transactionPool = new TransactionPool(_storage, _receiptStorage, _ethereumSigner, _logManager);
            var peers = GetPeers();
            var transactions = GetTransactions(peers);

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.AddPeer(peer);
            }

            for (var i = 0; i < transactions.Length; i++)
            {
                _transactionPool.AddTransaction(transactions[i], 1);
            }
        }

        [Test]
        public void Can_store_and_retrieve_receipt()
        {
            _receiptStorage = new PersistentTransactionReceiptStorage(new MemDb(), _specProvider);
            _transactionPool = new TransactionPool(_storage, _receiptStorage, _ethereumSigner, _logManager);
            Transaction tx = Build.A.Transaction.Signed(_ethereumSigner, TestObject.PrivateKeyA, 1).TestObject;
            TransactionReceipt txReceipt = Build.A.TransactionReceipt.WithState(TestObject.KeccakB)
                .WithTransactionHash(tx.Hash).TestObject;
            _transactionPool.AddReceipt(txReceipt);

            TransactionReceipt txReceiptRetrieved = _transactionPool.GetReceipt(tx.Hash);
            Assert.AreEqual(txReceipt.PostTransactionState, txReceiptRetrieved.PostTransactionState, "state");
        }

        private IDictionary<ISynchronizationPeer, PrivateKey> GetPeers(int limit = 100)
        {
            var peers = new Dictionary<ISynchronizationPeer, PrivateKey>();
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
            {
                for (var i = 0; i < limit; i++)
                {
                    rng.GetBytes(bytes);
                    var privateKey = new PrivateKey(bytes);
                    peers.Add(GetPeer(privateKey.PublicKey), privateKey);
                }
            }

            return peers;
        }

        private ISynchronizationPeer GetPeer(PublicKey publicKey)
            => new SynchronizationPeerMock(_remoteBlockTree, publicKey);

        private Transaction[] GetTransactions(IDictionary<ISynchronizationPeer, PrivateKey> peers,
            int transactionsPerPeer = 10)
        {
            var transactions = new List<Transaction>();
            foreach ((_, PrivateKey privateKey) in peers)
            {
                for (var i = 0; i < transactionsPerPeer; i++)
                {
                    transactions.Add(GetTransaction(Address.FromNumber(1), privateKey));
                }
            }

            return transactions.ToArray();
        }

        private Transaction GetTransaction(Address to, PrivateKey privateKey)
            => Build.A.Transaction
                .WithGasLimit(1000)
                .WithGasPrice(1)
                .To(to)
                .DeliveredBy(privateKey.PublicKey)
                .SignedAndResolved(_ethereumSigner, privateKey, 1)
                .TestObject;
    }
}