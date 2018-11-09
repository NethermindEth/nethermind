using System.Collections.Generic;
using System.Linq;
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
        private IBlockTree _remoteBlockTree;
        private ILogManager _logManager;
        private IEthereumSigner _ethereumSigner;
        private ISpecProvider _specProvider;
        private ITransactionPool _transactionPool;
        private ITransactionStorage _noTransactionStorage;
        private ITransactionStorage _inMemoryTransactionStorage;
        private ITransactionStorage _persistentTransactionStorage;
        private IReceiptStorage _noReceiptStorage;
        private IReceiptStorage _inMemoryReceiptStorage;
        private IReceiptStorage _persistentReceiptStorage;

        [SetUp]
        public void Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
            _logManager = NullLogManager.Instance;
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumSigner = new EthereumSigner(_specProvider, _logManager);
            _noTransactionStorage = new NullTransactionStorage();
            _inMemoryTransactionStorage = new InMemoryTransactionStorage();
            _persistentTransactionStorage = new PersistentTransactionStorage(new MemDb(), _specProvider);
            _noReceiptStorage = new NullReceiptStorage();
            _inMemoryReceiptStorage = new InMemoryReceiptStorage();
            _persistentReceiptStorage = new PersistentReceiptStorage(new MemDb(), _specProvider);
        }

        [Test]
        public void should_add_peers()
        {
            _transactionPool = CreatePool(_noTransactionStorage, _noReceiptStorage);
            var peers = GetPeers();

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            _transactionPool = CreatePool(_noTransactionStorage, _noReceiptStorage);
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
        public void should_add_pending_transactions()
        {
            _transactionPool = CreatePool(_noTransactionStorage, _noReceiptStorage);
            var transactions = AddTransactionsToPool();

            foreach (var transaction in transactions)
            {
                _transactionPool.AddTransaction(transaction, 1);
            }

            _transactionPool.GetPendingTransactions().Length.Should().Be(transactions.Length);
        }

        [Test]
        public void should_delete_pending_transactions()
        {
            _transactionPool = CreatePool(_noTransactionStorage, _noReceiptStorage);
            var transactions = AddTransactionsToPool();
            DeleteTransactionsFromPool(transactions);
            _transactionPool.GetPendingTransactions().Should().BeEmpty();
        }

        [Test]
        public void should_add_transactions_to_in_memory_storage()
        {
            var transactions = AddAndFilterTransactions(_inMemoryTransactionStorage);
            transactions.Pending.Count().Should().Be(transactions.Filtered.Count());
        }

        [Test]
        public void should_add_transactions_to_persistent_storage()
        {
            var transactions = AddAndFilterTransactions(_persistentTransactionStorage);
            transactions.Pending.Count().Should().Be(transactions.Filtered.Count());
        }

        [Test]
        public void should_add_all_transactions_to_storage_when_using_accept_all_filter()
        {
            var transactions = AddAndFilterTransactions(_inMemoryTransactionStorage, new AcceptAllTransactionFilter());
            transactions.Pending.Count().Should().Be(transactions.Filtered.Count());
        }

        [Test]
        public void should_not_add_any_transaction_to_storage_when_using_reject_all_filter()
        {
            var transactions = AddAndFilterTransactions(_inMemoryTransactionStorage, new RejectAllTransactionFilter());
            transactions.Filtered.Count().Should().Be(0);
            transactions.Pending.Count().Should().NotBe(transactions.Filtered.Count());
        }

        [Test]
        public void should_not_add_any_transaction_to_storage_when_using_accept_all_and_reject_all_filter()
        {
            var transactions = AddAndFilterTransactions(_inMemoryTransactionStorage,
                new AcceptAllTransactionFilter(), new RejectAllTransactionFilter());
            transactions.Filtered.Count().Should().Be(0);
            transactions.Pending.Count().Should().NotBe(transactions.Filtered.Count());
        }

        [Test]
        public void should_add_some_transactions_to_storage_when_using_accept_when_filter()
        {
            var filter = AcceptWhenTransactionFilter
                .Create()
                .Nonce(n => n >= 0)
                .GasPrice(p => p > 2 && p < 1500)
                .Build();
            var transactions = AddAndFilterTransactions(_inMemoryTransactionStorage, filter);
            transactions.Filtered.Count().Should().NotBe(0);
        }

        [Test]
        public void should_add_and_fetch_receipt_from_in_memory_storage()
        {
            _transactionPool = CreatePool(_noTransactionStorage, _inMemoryReceiptStorage);
            var transaction = GetSignedTransaction();
            var receipt = GetReceipt(transaction);
            _transactionPool.AddReceipt(receipt);
            var fetchedReceipt = _transactionPool.GetReceipt(transaction.Hash);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
        }

        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage()
        {
            _transactionPool = CreatePool(_noTransactionStorage, _persistentReceiptStorage);
            var transaction = GetSignedTransaction();
            var receipt = GetReceipt(transaction);
            _transactionPool.AddReceipt(receipt);
            var fetchedReceipt = _transactionPool.GetReceipt(transaction.Hash);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
        }

        private Transactions AddAndFilterTransactions(ITransactionStorage storage, params ITransactionFilter[] filters)
        {
            _transactionPool = CreatePool(storage, _noReceiptStorage);
            foreach (var filter in filters ?? Enumerable.Empty<ITransactionFilter>())
            {
                _transactionPool.AddFilter(filter);
            }

            var pendingTransactions = AddTransactionsToPool();
            var filteredTransactions = GetTransactionsFromStorage(storage, pendingTransactions);

            return new Transactions(pendingTransactions, filteredTransactions);
        }

        private IDictionary<ISynchronizationPeer, PrivateKey> GetPeers(int limit = 100)
        {
            var peers = new Dictionary<ISynchronizationPeer, PrivateKey>();
            for (var i = 0; i < limit; i++)
            {
                var privateKey = Build.A.PrivateKey.TestObject;
                peers.Add(GetPeer(privateKey.PublicKey), privateKey);
            }

            return peers;
        }

        private TransactionPool CreatePool(ITransactionStorage transactionStorage, IReceiptStorage receiptStorage)
            => new TransactionPool(transactionStorage, receiptStorage, _ethereumSigner, _logManager);

        private ISynchronizationPeer GetPeer(PublicKey publicKey)
            => new SynchronizationPeerMock(_remoteBlockTree, publicKey);

        private Transaction[] AddTransactionsToPool(int transactionsPerPeer = 10)
        {
            var transactions = GetTransactions(GetPeers(transactionsPerPeer));
            foreach (var transaction in transactions)
            {
                _transactionPool.AddTransaction(transaction, 1);
            }

            return transactions;
        }

        private void DeleteTransactionsFromPool(IEnumerable<Transaction> transactions)
        {
            foreach (var transaction in transactions)
            {
                _transactionPool.RemoveTransaction(transaction.Hash);
            }
        }

        private static IEnumerable<Transaction> GetTransactionsFromStorage(ITransactionStorage storage,
            IEnumerable<Transaction> transactions)
            => transactions.Select(t => storage.Get(t.Hash)).Where(t => !(t is null)).ToArray();

        private Transaction[] GetTransactions(IDictionary<ISynchronizationPeer, PrivateKey> peers,
            int transactionsPerPeer = 10)
        {
            var transactions = new List<Transaction>();
            foreach ((_, PrivateKey privateKey) in peers)
            {
                for (var i = 0; i < transactionsPerPeer; i++)
                {
                    transactions.Add(GetTransaction(privateKey, Address.FromNumber(i)));
                }
            }

            return transactions.ToArray();
        }

        private Transaction GetSignedTransaction(Address to = null)
            => GetTransaction(TestObject.PrivateKeyA, to);

        private Transaction GetTransaction(PrivateKey privateKey, Address to = null)
            => GetTransaction(0, 1, 1000, to, new byte[0], privateKey);

        private Transaction GetTransaction(UInt256 nonce, UInt256 gasLimit, UInt256 gasPrice, Address to, byte[] data,
            PrivateKey privateKey)
            => Build.A.Transaction
                .WithNonce(nonce)
                .WithGasLimit(gasLimit)
                .WithGasPrice(gasPrice)
                .WithData(data)
                .To(to)
                .DeliveredBy(privateKey.PublicKey)
                .SignedAndResolved(_ethereumSigner, privateKey, 1)
                .TestObject;

        private static TransactionReceipt GetReceipt(Transaction transaction)
            => Build.A.TransactionReceipt.WithState(TestObject.KeccakB)
                .WithTransactionHash(transaction.Hash).TestObject;

        private class Transactions
        {
            public IEnumerable<Transaction> Pending { get; }
            public IEnumerable<Transaction> Filtered { get; }

            public Transactions(IEnumerable<Transaction> pending, IEnumerable<Transaction> filtered)
            {
                Pending = pending;
                Filtered = filtered;
            }
        }
    }
}