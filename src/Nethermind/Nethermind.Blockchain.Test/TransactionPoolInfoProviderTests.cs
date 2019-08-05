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
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class TxPoolInfoProviderTests
    {
        private Address _address;
        private IStateProvider _stateProvider;
        private ITxPoolInfoProvider _infoProvider;
        private ITxPool _txPool;

        [SetUp]
        public void Setup()
        {
            _address = Address.FromNumber(1);
            _stateProvider = Substitute.For<IStateProvider>();
            _txPool = Substitute.For<ITxPool>();
            _infoProvider = new TxPoolInfoProvider(_stateProvider, _txPool);
        }

        [Test]
        public void should_return_valid_pending_and_queued_transactions()
        {
            var nonce = 3;
            _stateProvider.GetNonce(_address).Returns(new UInt256(nonce));
            var transactions = GetTransactions();
            
            _txPool.GetPendingTransactions().Returns(transactions);
            var info = _infoProvider.GetInfo();

            info.Pending.Count.Should().Be(1);
            info.Queued.Count.Should().Be(1);

            var pending = info.Pending.First();
            pending.Key.Should().Be(_address);
            pending.Value.Count.Should().Be(3);
            pending.Value.Sum(v => v.Value.Length).Should().Be(4);
            VerifyNonceAndTransactions(pending.Value.ElementAt(0), 3, 2);
            VerifyNonceAndTransactions(pending.Value.ElementAt(1), 4, 1);
            VerifyNonceAndTransactions(pending.Value.ElementAt(2), 5, 1);

            var queued = info.Queued.First();
            queued.Key.Should().Be(_address);
            queued.Value.Count.Should().Be(4);
            queued.Value.Sum(v => v.Value.Length).Should().Be(4);
            VerifyNonceAndTransactions(queued.Value.ElementAt(0), 1, 1);
            VerifyNonceAndTransactions(queued.Value.ElementAt(1), 2, 1);
            VerifyNonceAndTransactions(queued.Value.ElementAt(2), 8, 1);
            VerifyNonceAndTransactions(queued.Value.ElementAt(3), 9, 1);
        }

        private void VerifyNonceAndTransactions(KeyValuePair<ulong, Transaction[]> nonceAndTransaction,
            ulong nonce, int transactionsCount)
        {
            nonceAndTransaction.Key.Should().Be(nonce);
            nonceAndTransaction.Value.Length.Should().Be(transactionsCount);
        }

        private Transaction[] GetTransactions()
            => new[]
            {
                GetTransaction(1),
                GetTransaction(2),
                GetTransaction(3),
                GetTransaction(3),
                GetTransaction(4),
                GetTransaction(5),
                GetTransaction(8),
                GetTransaction(9)
            };

        private Transaction GetTransaction(UInt256 nonce)
            => Build.A.Transaction
                .WithNonce(nonce)
                .WithSenderAddress(_address)
                .TestObject;
    }
}