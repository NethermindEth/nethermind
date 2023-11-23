// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Network.Benchmarks
{
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
            Session session = new Session(8545, Substitute.For<IChannel>(), Substitute.For<IDisconnectsAnalyzer>(), LimboLogs.Instance);
            session.RemoteNodeId = TestItem.PublicKeyA;
            session.RemoteHost = "127.0.0.1";
            session.RemotePort = 30303;
            _ser = new MessageSerializationService();
            _ser.Register(new TransactionsMessageSerializer());
            _ser.Register(new StatusMessageSerializer());
            NodeStatsManager stats = new NodeStatsManager(TimerFactory.Default, LimboLogs.Instance);
            var ecdsa = new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance);
            var tree = Build.A.BlockTree().TestObject;
            var stateProvider = new WorldState(new TrieStore(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance);
            var specProvider = MainnetSpecProvider.Instance;
            TxPool.TxPool txPool = new TxPool.TxPool(
                ecdsa,
                new BlobTxStorage(),
                new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(MainnetSpecProvider.Instance), tree, stateProvider),
                new TxPoolConfig(),
                new TxValidator(TestBlockchainIds.ChainId),
                LimboLogs.Instance,
                new TransactionComparerProvider(specProvider, tree).GetDefaultComparer());
            ISyncServer syncSrv = Substitute.For<ISyncServer>();
            BlockHeader head = Build.A.BlockHeader.WithNumber(1).TestObject;
            syncSrv.Head.Returns(head);
            _handler = new Eth62ProtocolHandler(session, _ser, stats, syncSrv, txPool, Consensus.ShouldGossip.Instance, LimboLogs.Instance);
            _handler.DisableTxFiltering();

            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.NetworkId = 1;
            IByteBuffer bufStatus = _ser.ZeroSerialize(statusMessage);
            _zeroPacket = new ZeroPacket(bufStatus);
            _zeroPacket.PacketType = bufStatus.ReadByte();

            _handler.HandleMessage(_zeroPacket);

            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            _txMsg = new TransactionsMessage(new[] { tx });
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
