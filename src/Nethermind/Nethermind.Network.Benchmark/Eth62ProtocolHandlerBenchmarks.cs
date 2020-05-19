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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
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
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using NSubstitute;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class Eth62ProtocolHandlerBenchmarks
    {
        private Eth62ProtocolHandler _handler;
        private ZeroPacket _zeroPacket;
        private MessageSerializationService _ser;
        private TransactionsMessage _txMsg;

        [GlobalSetup]
        public void SetUp()
        {
            Console.WriteLine("AAA");
            Session session = new Session(8545, LimboLogs.Instance, Substitute.For<IChannel>());
            session.RemoteNodeId = TestItem.PublicKeyA;
            session.RemoteHost = "127.0.0.1";
            session.RemotePort = 30303;
            _ser = new MessageSerializationService();
            _ser.Register(new TransactionsMessageSerializer());
            _ser.Register(new StatusMessageSerializer());
            NodeStatsManager stats = new NodeStatsManager(new StatsConfig(), LimboLogs.Instance);

            var ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            TxPool.TxPool txPool = new TxPool.TxPool(NullTxStorage.Instance, Timestamper.Default, ecdsa, MainnetSpecProvider.Instance, new TxPoolConfig(), Substitute.For<IStateProvider>(), LimboLogs.Instance);
            ISyncServer syncSrv = Substitute.For<ISyncServer>();
            BlockHeader head = Build.A.BlockHeader.WithNumber(1).TestObject;
            syncSrv.Head.Returns(head);
            _handler = new Eth62ProtocolHandler(session, _ser, stats, syncSrv, txPool, LimboLogs.Instance);
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

            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            _txMsg = new TransactionsMessage(new[] {tx});
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

        [Benchmark]
        public void JustSerialize()
        {
            _ser.ZeroSerialize(_txMsg);
        }

        [Benchmark]
        public void SerializeAndCreatePacket()
        {
            IByteBuffer buf = _ser.ZeroSerialize(_txMsg);
            _zeroPacket = new ZeroPacket(buf);
            _zeroPacket.PacketType = buf.ReadByte();
            _zeroPacket.PacketType = Eth62MessageCode.Transactions;
        }
    }
}