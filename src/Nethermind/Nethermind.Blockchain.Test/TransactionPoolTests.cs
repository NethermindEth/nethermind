using System.Collections.Generic;
using System.Security.Cryptography;
using FluentAssertions;
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
        private Block _genesisBlock;
        private ILogManager _logManager;
        private IEthereumSigner _ethereumSigner;
        private ISpecProvider _specProvider;
        private ITransactionPool _transactionPool;
        private ITransactionStorage _noStorage;
        private ITransactionStorage _persistentStorage;
        private ITransactionStorage _inMemoryStorage;
        private IReceiptStorage _noReceiptStorage;
        private IReceiptStorage _persistentReceiptStorage;
        private IReceiptStorage _inMemoryReceiptStorage;
        private IBlockTree _remoteBlockTree;

        [SetUp]
        public void Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _logManager = NullLogManager.Instance;
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumSigner = new EthereumSigner(_specProvider, _logManager);
            _noStorage = new NoTransactionStorage();
            _inMemoryStorage = new InMemoryTransactionStorage();
            _persistentStorage = new PersistentTransactionStorage(new MemDb(), _specProvider);
            _noReceiptStorage = new NoReceiptStorage();
            _persistentReceiptStorage = new PersistentReceiptStorage(new MemDb(), _specProvider);
            _inMemoryReceiptStorage = new InMemoryReceiptStorage();
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
        }

        [Test]
        public void should_add_peers()
        {
            _transactionPool = new TransactionPool(_noStorage, _noReceiptStorage, _ethereumSigner, _logManager);
            var peers = GetPeers();

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            _transactionPool = new TransactionPool(_noStorage, _noReceiptStorage, _ethereumSigner, _logManager);
            var peers = GetPeers();

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.AddPeer(peer);
            }

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.DeletePeer(peer.NodeId);
            }
        }

        [Test]
        public void should_add_transactions()
        {
            _transactionPool = new TransactionPool(_noStorage, _noReceiptStorage, _ethereumSigner, _logManager);
            var transactions = GetTransactions(GetPeers());

            foreach (var transaction in transactions)
            {
                _transactionPool.AddTransaction(transaction, 1);
            }

            _transactionPool.PendingTransactions.Length.Should().Be(transactions.Length);
        }

        [Test]
        public void should_add_transactions_to_in_memory_storage()
        {
            _transactionPool = new TransactionPool(_inMemoryStorage, _noReceiptStorage, _ethereumSigner, _logManager);
            var transactions = GetTransactions(GetPeers());

            foreach (var transaction in transactions)
            {
                _transactionPool.AddTransaction(transaction, 1);
            }

            var storedTransactions = new List<Transaction>();
            foreach (var transaction in transactions)
            {
                storedTransactions.Add(_inMemoryStorage.Get(transaction.Hash));
            }

            storedTransactions.Count.Should().Be(transactions.Length);
        }

        [Test]
        public void should_add_transactions_to_persistent_storage()
        {
            _transactionPool = new TransactionPool(_persistentStorage, _noReceiptStorage, _ethereumSigner, _logManager);
            var transactions = GetTransactions(GetPeers());

            foreach (var transaction in transactions)
            {
                _transactionPool.AddTransaction(transaction, 1);
            }

            var storedTransactions = new List<Transaction>();
            foreach (var transaction in transactions)
            {
                storedTransactions.Add(_persistentStorage.Get(transaction.Hash));
            }

            storedTransactions.Count.Should().Be(transactions.Length);
        }

        [Test]
        public void should_delete_transactions()
        {
            _transactionPool = new TransactionPool(_noStorage, _noReceiptStorage, _ethereumSigner, _logManager);
            var transactions = GetTransactions(GetPeers());

            foreach (var transaction in transactions)
            {
                _transactionPool.AddTransaction(transaction, 1);
            }

            foreach (var transaction in transactions)
            {
                _transactionPool.DeleteTransaction(transaction.Hash);
            }

            _transactionPool.PendingTransactions.Should().BeEmpty();
        }

        [Test]
        public void should_store_and_get_valid_receipt()
        {
            _noReceiptStorage = new PersistentReceiptStorage(new MemDb(), _specProvider);
            _transactionPool = new TransactionPool(_noStorage, _noReceiptStorage, _ethereumSigner, _logManager);
            var transaction = Build.A.Transaction.Signed(_ethereumSigner, TestObject.PrivateKeyA, 1).TestObject;
            var receipt = Build.A.TransactionReceipt.WithState(TestObject.KeccakB)
                .WithTransactionHash(transaction.Hash).TestObject;

            _transactionPool.AddReceipt(receipt);

            var fetchedReceipt = _transactionPool.GetReceipt(transaction.Hash);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
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
                    transactions.Add(GetTransaction(Address.FromNumber(i), privateKey));
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