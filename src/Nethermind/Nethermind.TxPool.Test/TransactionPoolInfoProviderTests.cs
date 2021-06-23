//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    [TestFixture]
    public class TxPoolInfoProviderTests
    {
        private Address _address;
        private IAccountStateProvider _stateReader;
        private ITxPoolInfoProvider _infoProvider;
        private ITxPool _txPool;

        [SetUp]
        public void Setup()
        {
            _address = Address.FromNumber(1);
            _stateReader = Substitute.For<IAccountStateProvider>();
            _txPool = Substitute.For<ITxPool>();
            _infoProvider = new TxPoolInfoProvider(_stateReader, _txPool);
        }

        [Test]
        public void should_return_valid_pending_and_queued_transactions()
        {
            uint nonce = 3;
            _stateReader.GetAccount(_address).Returns(Account.TotallyEmpty.WithChangedNonce(nonce));
            var transactions = GetTransactions();

            _txPool.GetPendingTransactionsBySender()
                .Returns(new Dictionary<Address, Transaction[]> {{_address, transactions}});
            var info = _infoProvider.GetInfo();

            info.Pending.Count.Should().Be(1);
            info.Queued.Count.Should().Be(1);

            var pending = info.Pending.First();
            pending.Key.Should().Be(_address);
            pending.Value.Count.Should().Be(3);
            VerifyNonceAndTransactions(pending.Value, 3);
            VerifyNonceAndTransactions(pending.Value, 4);
            VerifyNonceAndTransactions(pending.Value, 5);

            var queued = info.Queued.First();
            queued.Key.Should().Be(_address);
            queued.Value.Count.Should().Be(4);
            VerifyNonceAndTransactions(queued.Value, 1);
            VerifyNonceAndTransactions(queued.Value, 2);
            VerifyNonceAndTransactions(queued.Value, 8);
            VerifyNonceAndTransactions(queued.Value, 9);
        }

        private void VerifyNonceAndTransactions(IDictionary<ulong, Transaction> transactionNonce, ulong nonce)
        {
            transactionNonce[nonce].Nonce.Should().Be(nonce);
        }

        private Transaction[] GetTransactions()
            => new[]
            {
                GetTransaction(1), GetTransaction(2), GetTransaction(3), GetTransaction(4), GetTransaction(5),
                GetTransaction(8), GetTransaction(9)
            };

        private Transaction GetTransaction(UInt256 nonce)
            => Build.A.Transaction
                .WithNonce(nonce)
                .WithSenderAddress(_address)
                .TestObject;
    }
}
