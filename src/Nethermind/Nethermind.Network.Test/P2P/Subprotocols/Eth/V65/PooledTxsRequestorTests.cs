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

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class PooledTxsRequestorTests
    {
        private readonly ITxPool _txPool = Substitute.For<ITxPool>();
        

        [Test]
        public void filter_properly_newPooledTxHashes()
        {
            PooledTxsRequestor requestor = new PooledTxsRequestor(_txPool);
            requestor.FilterHashes(new List<Keccak>{TestItem.KeccakA, TestItem.KeccakD});

            IList<Keccak> request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            IList<Keccak> expected = new List<Keccak>{TestItem.KeccakB, TestItem.KeccakC};
            IList<Keccak> response = requestor.FilterHashes(request);
            response.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void filter_properly_already_pending_hashes()
        {
            PooledTxsRequestor requestor = new PooledTxsRequestor(_txPool);
            requestor.FilterHashes(new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC});
            
            IList<Keccak> request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            IList<Keccak> expected = new List<Keccak>{};
            IList<Keccak> response = requestor.FilterHashes(request);
            response.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void filter_properly_discovered_hashes()
        {
            PooledTxsRequestor requestor = new PooledTxsRequestor(_txPool);

            IList<Keccak> request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            IList<Keccak> expected = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            IList<Keccak> response = requestor.FilterHashes(request);
            response.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void can_handle_empty_argument()
        {
            PooledTxsRequestor requestor = new PooledTxsRequestor(_txPool);
            requestor.FilterHashes(new List<Keccak>()).Should().BeEmpty();
        }

        [Test]
        public void filter_properly_hashes_present_in_hashCache()
        {
            ITxPool txPool = Substitute.For<ITxPool>();
            txPool.IsInHashCache(Arg.Any<Keccak>()).Returns(true);
            PooledTxsRequestor requestor = new PooledTxsRequestor(txPool);
            
            IList<Keccak> request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB};
            IList<Keccak> expected = new List<Keccak>{};
            IList<Keccak> response = requestor.FilterHashes(request);
            response.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void can_request_transactions()
        {
            PooledTxsRequestor requestor = new PooledTxsRequestor(_txPool);
            ISession session = Substitute.For<ISession>();
            IMessageSerializationService svc = Build.A.SerializationService().WithEth65().TestObject;

            Eth65ProtocolHandler eth65ProtocolHandler = new Eth65ProtocolHandler(
                session,
                svc,
                Substitute.For<INodeStatsManager>(),
                Substitute.For<ISyncServer>(),
                _txPool,
                requestor,
                Substitute.For<ISpecProvider>(),
                Substitute.For<ILogManager>());

            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            StatusMessage statusMsg = new StatusMessage {GenesisHash = genesisBlock.Hash, BestHash = genesisBlock.Hash};
            IByteBuffer statusPacket = svc.ZeroSerialize(statusMsg);
            statusPacket.ReadByte();
            eth65ProtocolHandler.HandleMessage(new ZeroPacket(statusPacket) {PacketType = 0});
            
            IList<Keccak> request = new List<Keccak>{TestItem.KeccakA, TestItem.KeccakB};
            NewPooledTransactionHashesMessage msg = new NewPooledTransactionHashesMessage(request.ToArray());
            IByteBuffer packet = svc.ZeroSerialize(msg);
            packet.ReadByte();
            eth65ProtocolHandler.HandleMessage(new ZeroPacket(packet) {PacketType = Eth65MessageCode.NewPooledTransactionHashes});
            
            session.Received().DeliverMessage(Arg.Is<GetPooledTransactionsMessage>(m => m.Hashes.Count == 2));
        }
    }
}
