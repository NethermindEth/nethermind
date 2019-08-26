using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class TransactionFilterTests
    {
        private IEthereumEcdsa _ethereumEcdsa;
        private ISpecProvider _specProvider;
        private ITxFilter _filter;

        [SetUp]
        public void Setup()
        {
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider, NullLogManager.Instance);
        }

        [Test]
        public void should_accept_any_transaction_when_using_accept_all_filter()
        {
            _filter = new AcceptAllTxFilter();
            var transactions = GetTransactions();
            var addedTransactions = ApplyFilter(transactions);
            addedTransactions.Length.Should().Be(transactions.Length);
        }

        [Test]
        public void should_not_accept_any_transaction_when_using_reject_all_filter()
        {
            _filter = new RejectAllTxFilter();
            var transactions = GetTransactions();
            var addedTransactions = ApplyFilter(transactions);
            addedTransactions.Should().BeEmpty();
        }

        [Test]
        public void should_add_some_transactions_to_storage_when_using_accept_when_filter()
        {
            _filter = AcceptWhenTxFilter
                .Create()
                .Nonce(n => n >= 0)
                .GasPrice(p => p > 2 && p < 1500)
                .Build();
            var transactions = GetTransactions();
            var addedTransactions = ApplyFilter(transactions);
            addedTransactions.Should().NotBeEmpty();
        }

        private Transaction[] ApplyFilter(IEnumerable<Transaction> transactions)
            => transactions.Where(t => _filter.IsValid(t)).ToArray();

        private Transaction[] GetTransactions()
            => new[]
            {
                GetTransaction(0, 1000, 10, Address.Zero, new byte[0], TestItem.PrivateKeyA),
                GetTransaction(0, 500, 2, Address.FromNumber(1), new byte[0], TestItem.PrivateKeyB),
                GetTransaction(1, 2000, 50, Address.FromNumber(3), new byte[0], TestItem.PrivateKeyC),
                GetTransaction(2, 10000, 100, Address.FromNumber(2), new byte[0], TestItem.PrivateKeyD),
                GetTransaction(3, 4000, 5, Address.Zero, new byte[0], TestItem.PrivateKeyC)
            };

        private Transaction GetTransaction(UInt256 nonce, long gasLimit,
            UInt256 gasPrice, Address to, byte[] data, PrivateKey privateKey)
            => Build.A.Transaction
                .WithNonce(nonce)
                .WithGasLimit(gasLimit)
                .WithGasPrice(gasPrice)
                .WithData(data)
                .To(to)
                .DeliveredBy(privateKey.PublicKey)
                .SignedAndResolved(_ethereumEcdsa, privateKey, 1)
                .TestObject;
    }
}