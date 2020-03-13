//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using NSubstitute;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [ShortRunJob]
    public class Eth62ProtocolHandlerBenchmarks
    {
        private Eth62ProtocolHandler _handler;
        private ZeroPacket _zeroPacket;
        private MessageSerializationService _ser;
        private TransactionsMessage _txMsg;

        private class SyncServerMock : ISyncServer
        {
            private readonly BlockHeader _header;

            public SyncServerMock(BlockHeader header)
            {
                _header = header;
            }
            
            public void HintBlock(Keccak hash, long number, Node receivedFrom)
            {
                throw new System.NotImplementedException();
            }

            public void AddNewBlock(Block block, Node node)
            {
                throw new System.NotImplementedException();
            }

            public TxReceipt[][] GetReceipts(IList<Keccak> blockHashes)
            {
                throw new System.NotImplementedException();
            }

            public Block Find(Keccak hash)
            {
                throw new System.NotImplementedException();
            }

            public Keccak FindHash(long number)
            {
                throw new System.NotImplementedException();
            }

            public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse)
            {
                throw new System.NotImplementedException();
            }

            public byte[][] GetNodeData(IList<Keccak> keys)
            {
                throw new System.NotImplementedException();
            }

            public int GetPeerCount()
            {
                throw new System.NotImplementedException();
            }

            public int ChainId { get; }
            public BlockHeader Genesis { get; }
            public BlockHeader Head => _header;
        }
        
        [GlobalSetup]
        public void SetUp()
        {
            ILogManager logManager = ForBenchmarks.Instance;
            Session session = new Session(8545, logManager, Substitute.For<IChannel>());
            session.RemoteNodeId = TestItem.PublicKeyA;
            session.RemoteHost = "127.0.0.1";
            session.RemotePort = 30303;
            var node = session.Node;
            Console.WriteLine(node.Host); // just to invoke node getter
            
            _ser = new MessageSerializationService();
            _ser.Register(new TransactionsMessageSerializer());
            _ser.Register(new StatusMessageSerializer());
            NodeStatsManager stats = new NodeStatsManager(new StatsConfig(), logManager);
            
            var ecdsa = new EthereumEcdsa(MainNetSpecProvider.Instance, logManager);
            TxPool.TxPool txPool = new TxPool.TxPool(NullTxStorage.Instance, Timestamper.Default, ecdsa, MainNetSpecProvider.Instance, new TxPoolConfig(), Substitute.For<IStateProvider>(), logManager);
            BlockHeader head = Build.A.BlockHeader.WithNumber(1).TestObject;
            ISyncServer syncSrv = new SyncServerMock(head);
            _handler = new Eth62ProtocolHandler(session, _ser, stats, syncSrv, txPool, logManager);
            _handler.DisableTxFiltering();
            
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.ChainId = 1;
            IByteBuffer bufStatus = _ser.ZeroSerialize(statusMessage);
            _zeroPacket = new ZeroPacket(bufStatus);
            _zeroPacket.PacketType = bufStatus.ReadByte();

            _handler.HandleMessage(_zeroPacket);
            
            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA, 1).TestObject;
            _txMsg = new TransactionsMessage(tx);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            IByteBuffer buf = _ser.ZeroSerialize(_txMsg);
            _zeroPacket = new ZeroPacket(buf);
            _zeroPacket.PacketType = buf.ReadByte();
            _zeroPacket.PacketType = Eth62MessageCode.Transactions;
            _handler.HandleMessage(_zeroPacket);
        }
    }
}