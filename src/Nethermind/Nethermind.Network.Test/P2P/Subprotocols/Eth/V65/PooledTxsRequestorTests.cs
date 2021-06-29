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
// 

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
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
        private IReadOnlyList<Keccak> _request;
        private IList<Keccak> _expected;
        private IList<Keccak> _response;
        
        
        [Test]
        public void filter_properly_newPooledTxHashes()
        {
            _response = new List<Keccak>();
            _requestor = new PooledTxsRequestor(_txPool);
            _requestor.RequestTransactions(_doNothing, new List<Keccak>{TestItem.KeccakA, TestItem.KeccakD});

            _request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            _expected = new List<Keccak>{TestItem.KeccakB, TestItem.KeccakC};
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEquivalentTo(_expected);
        }

        [Test]
        public void filter_properly_already_pending_hashes()
        {
            _response = new List<Keccak>();
            _requestor = new PooledTxsRequestor(_txPool);
            _requestor.RequestTransactions(_doNothing, new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC});
            
            _request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEmpty();
        }
        
        [Test]
        public void filter_properly_discovered_hashes()
        {
            _response = new List<Keccak>();
            _requestor = new PooledTxsRequestor(_txPool);
        
            _request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            _expected = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEquivalentTo(_expected);
        }
        
        [Test]
        public void can_handle_empty_argument()
        {
            _response = new List<Keccak>();
            _requestor = new PooledTxsRequestor(_txPool);
            _requestor.RequestTransactions(Send, new List<Keccak>());
            _response.Should().BeEmpty();
        }
        
        [Test]
        public void filter_properly_hashes_present_in_hashCache()
        {
            _response = new List<Keccak>();
            ITxPool txPool = Substitute.For<ITxPool>();
            txPool.IsKnown(Arg.Any<Keccak>()).Returns(true);
            _requestor = new PooledTxsRequestor(txPool);
            
            _request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB};
            _expected = new List<Keccak>{};
            _requestor.RequestTransactions(Send, _request);
            _response.Should().BeEquivalentTo(_expected);
        }
        
        private void Send(GetPooledTransactionsMessage msg)
        {
            _response = msg.Hashes;
        }
    }
}
