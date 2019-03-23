/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
        private IEthereumEcdsa _ethereumEcdsa;
        private ISpecProvider _specProvider;
        private ITransactionPool _transactionPool;
        private ITransactionStorage _noTransactionStorage;
        private ITransactionStorage _inMemoryTransactionStorage;
        private ITransactionStorage _persistentTransactionStorage;

        [SetUp]
        public void Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
            _logManager = LimboLogs.Instance;
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider, _logManager);
            _noTransactionStorage = NullTransactionStorage.Instance;
            _inMemoryTransactionStorage = new InMemoryTransactionStorage();
            _persistentTransactionStorage = new PersistentTransactionStorage(new MemDb(), _specProvider);
        }

        [Test]
        public void should_add_peers()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
            var peers = GetPeers();

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
            var peers = GetPeers();

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.AddPeer(peer);
            }

            foreach ((ISynchronizationPeer peer, _) in peers)
            {
                _transactionPool.RemovePeer(peer.Node.Id);
            }
        }

        [Test]
        public void should_ignore_transactions_with_different_chain_id()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
            EthereumEcdsa ecdsa = new EthereumEcdsa(MainNetSpecProvider.Instance, _logManager);
            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA, MainNetSpecProvider.ByzantiumBlockNumber).TestObject;
            AddTransactionResult result = _transactionPool.AddTransaction(tx, 1);
            _transactionPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTransactionResult.InvalidChainId);
        }
        
        [Test]
        public void should_ignore_old_scheme_signatures()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).TestObject;
            AddTransactionResult result = _transactionPool.AddTransaction(tx, 1);
            _transactionPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTransactionResult.OldScheme);
        }
        
        [Test]
        public void should_ignore_already_known()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, RopstenSpecProvider.ByzantiumBlockNumber).TestObject;
            AddTransactionResult result1 = _transactionPool.AddTransaction(tx, 1);
            AddTransactionResult result2 = _transactionPool.AddTransaction(tx, 1);
            _transactionPool.GetPendingTransactions().Length.Should().Be(1);
            result1.Should().Be(AddTransactionResult.Added);
            result2.Should().Be(AddTransactionResult.AlreadyKnown);
        }
        
        [Test]
        public void should_add_valid_transactions()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, RopstenSpecProvider.ByzantiumBlockNumber).TestObject;
            AddTransactionResult result = _transactionPool.AddTransaction(tx, 1);
            _transactionPool.GetPendingTransactions().Length.Should().Be(1);
            result.Should().Be(AddTransactionResult.Added);
        }

        [Test]
        public void should_add_pending_transactions()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
            var transactions = AddTransactionsToPool();
            _transactionPool.GetPendingTransactions().Length.Should().Be(transactions.Length);
        }

        [Test]
        public void should_delete_pending_transactions()
        {
            _transactionPool = CreatePool(_noTransactionStorage);
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

        private Transactions AddAndFilterTransactions(ITransactionStorage storage, params ITransactionFilter[] filters)
        {
            _transactionPool = CreatePool(storage);
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

        private TransactionPool CreatePool(ITransactionStorage transactionStorage)
            => new TransactionPool(transactionStorage, new PendingTransactionThresholdValidator(),
                new Timestamp(), _ethereumEcdsa, _specProvider, _logManager);

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
                .SignedAndResolved(_ethereumEcdsa, privateKey, RopstenSpecProvider.ByzantiumBlockNumber)
                .TestObject;

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