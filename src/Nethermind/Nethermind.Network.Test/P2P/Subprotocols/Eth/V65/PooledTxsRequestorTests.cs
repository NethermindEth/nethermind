// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    public class PooledTxsRequestorTests
    {
        private readonly ITxPool _txPool = Substitute.For<ITxPool>();
        private readonly Action<GetPooledTransactionsMessage> _doNothing = Substitute.For<Action<GetPooledTransactionsMessage>>();
        private IPooledTxsRequestor _requestor;
        private IReadOnlyList<Hash256> _request;
        private IList<Hash256> _expected;
        private IReadOnlyList<Hash256> _response;


        [Test]
        public void filter_properly_newPooledTxHashes()
        {
            _response = new List<Hash256>();
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig());
            _requestor.RequestTransactions(_doNothing, new List<Hash256> { TestItem.KeccakA, TestItem.KeccakD });

            _request = new List<Hash256> { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _expected = new List<Hash256> { TestItem.KeccakB, TestItem.KeccakC };
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEquivalentTo(_expected);
        }

        [Test]
        public void filter_properly_already_pending_hashes()
        {
            _response = new List<Hash256>();
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig());
            _requestor.RequestTransactions(_doNothing, new List<Hash256> { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC });

            _request = new List<Hash256> { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEmpty();
        }

        [Test]
        public void filter_properly_discovered_hashes()
        {
            _response = new List<Hash256>();
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig());

            _request = new List<Hash256> { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _expected = new List<Hash256> { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC };
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEquivalentTo(_expected);
        }

        [Test]
        public void can_handle_empty_argument()
        {
            _response = new List<Hash256>();
            _requestor = new PooledTxsRequestor(_txPool, new TxPoolConfig());
            _requestor.RequestTransactions(Send, new List<Hash256>());
            _response.Should().BeEmpty();
        }

        [Test]
        public void filter_properly_hashes_present_in_hashCache()
        {
            _response = new List<Hash256>();
            ITxPool txPool = Substitute.For<ITxPool>();
            txPool.IsKnown(Arg.Any<Hash256>()).Returns(true);
            _requestor = new PooledTxsRequestor(txPool, new TxPoolConfig());

            _request = new List<Hash256> { TestItem.KeccakA, TestItem.KeccakB };
            _expected = new List<Hash256> { };
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEquivalentTo(_expected);
        }

        private void Send(GetPooledTransactionsMessage msg)
        {
            _response = msg.Hashes.ToList();
        }
    }
}
