// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    public class PooledTxsRequestorTests
    {
        private readonly ITxPool _txPool = Substitute.For<ITxPool>();
        private readonly ISpecProvider _specProvider = Substitute.For<ISpecProvider>();
        private readonly Action<GetPooledTransactionsMessage> _doNothing = static msg => msg.Dispose();
        private IPooledTxsRequestor _requestor;
        private ArrayPoolList<Hash256> _response;

        [TearDown]
        public void TearDown()
        {
            _response?.Dispose();
        }

        [Test]
        public void filter_properly_newPooledTxHashes()
        {
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig(), _specProvider);
            using var skipped = new ArrayPoolList<Hash256>(2) { TestItem.KeccakA, TestItem.KeccakD };
            _requestor.RequestTransactions(_doNothing, skipped, Arg.Any<Guid>());

            using var request = new ArrayPoolList<Hash256>(3) { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            using var expected = new ArrayPoolList<Hash256>(3) { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _requestor.RequestTransactions(Send, request, Arg.Any<Guid>());
            _response.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void filter_properly_already_pending_hashes()
        {
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig(), _specProvider);
            using var skipped = new ArrayPoolList<Hash256>(3) { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _requestor.RequestTransactions(_doNothing, skipped, Arg.Any<Guid>());

            using var request = new ArrayPoolList<Hash256>(3) { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _requestor.RequestTransactions(Send, request, Arg.Any<Guid>());
            _response.Should().BeEquivalentTo(request);
        }

        [Test]
        public void filter_properly_discovered_hashes()
        {
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig(), _specProvider);

            using var request = new ArrayPoolList<Hash256>(3) { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            using var expected = new ArrayPoolList<Hash256>(3) { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _requestor.RequestTransactions(Send, request, Arg.Any<Guid>());
            _response.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void can_handle_empty_argument()
        {
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig(), _specProvider);
            using var skipped = new ArrayPoolList<Hash256>(0);
            _requestor.RequestTransactions(Send, skipped, Arg.Any<Guid>());
            _response.Should().BeEmpty();
        }

        [Test]
        public void filter_properly_hashes_present_in_hashCache()
        {
            ITxPool txPool = Substitute.For<ITxPool>();
            txPool.IsKnown(Arg.Any<Hash256>()).Returns(true);
            _requestor = new PooledTxsRequestor(txPool, new TxPoolConfig(), _specProvider);

            using var request = new ArrayPoolList<Hash256>(2) { TestItem.KeccakA, TestItem.KeccakB };
            using var expected = new ArrayPoolList<Hash256>(0) { };
            _requestor.RequestTransactions(Send, request, Arg.Any<Guid>());
            _response.Should().BeEquivalentTo(expected);
        }

        private void Send(GetPooledTransactionsMessage msg)
        {
            _response?.Dispose();
            using (msg)
            {
                _response = msg.Hashes.ToPooledList();
            }
        }
    }
}
