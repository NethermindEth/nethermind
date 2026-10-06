// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Synchronization;
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
            Session session = new(8545, Substitute.For<IChannel>(), Substitute.For<IDisconnectsAnalyzer>(), LimboLogs.Instance);
            session.RemoteNodeId = TestItem.PublicKeyA;
            session.RemoteHost = "127.0.0.1";
            session.RemotePort = 30303;
            _ser = new MessageSerializationService(
                SerializerInfo.Create(new TransactionsMessageSerializer()),
                SerializerInfo.Create(new StatusMessageSerializer())
                );
            NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance);
            EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
            BlockTree tree = Build.A.BlockTree().TestObject;
            IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
            MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
            TxPool.TxPool txPool = new(
                ecdsa,
                new BlobTxStorage(),
                new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(MainnetSpecProvider.Instance), tree, stateProvider),
                new TxPoolConfig(),
                new TxValidator(TestBlockchainIds.ChainId),
                new SpecChangeTxValidator(TestBlockchainIds.ChainId),
                LimboLogs.Instance,
                new TransactionComparerProvider(specProvider, tree).GetDefaultComparer());
            ISyncServer syncSrv = Substitute.For<ISyncServer>();
            BlockHeader head = Build.A.BlockHeader.WithNumber(1).TestObject;
            syncSrv.Head.Returns(head);
            _handler = new Eth62ProtocolHandler(session, _ser, stats, syncSrv, RunImmediatelyScheduler.Instance, txPool, Consensus.ShouldGossip.Instance, LimboLogs.Instance);
            _handler.DisableTxFiltering();

            StatusMessage statusMessage = new();
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.NetworkId = 1;
            using PooledBuffer bufStatus = _ser.ZeroSerialize(statusMessage);
            _zeroPacket = new ZeroPacket(Wrap(bufStatus));
            // The adaptive prefix byte (0x80 for type 0) is skipped by Wrap; the packet type is the decoded value.
            _zeroPacket.PacketType = Eth62MessageCode.Status;

            _handler.HandleMessage(_zeroPacket);

            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            _txMsg = new TransactionsMessage(new[] { tx }.ToPooledList());
        }

        [GlobalCleanup]
        public void Cleanup()
        {
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            using PooledBuffer buf = _ser.ZeroSerialize(_txMsg);
            _zeroPacket = new ZeroPacket(Wrap(buf));
            _zeroPacket.PacketType = Eth62MessageCode.Transactions;
            _handler.HandleMessage(_zeroPacket);
        }

        [Benchmark]
        public int JustSerialize()
        {
            using PooledBuffer buf = _ser.ZeroSerialize(_txMsg);
            return buf.Length;
        }

        [Benchmark]
        public void SerializeAndCreatePacket()
        {
            using PooledBuffer buf = _ser.ZeroSerialize(_txMsg);
            _zeroPacket = new ZeroPacket(Wrap(buf));
            _zeroPacket.PacketType = Eth62MessageCode.Transactions;
        }

        private static IByteBuffer Wrap(PooledBuffer buffer)
        {
            if (!MemoryMarshal.TryGetArray(buffer.ReadOnlyMemory, out ArraySegment<byte> segment) ||
                segment.Array is null)
            {
                throw new InvalidOperationException("Pooled message buffer is not array-backed.");
            }

            // Skips the adaptive packet-type byte, like the old ReadByte did.
            return Unpooled.WrappedBuffer(segment.Array, segment.Offset + 1, segment.Count - 1);
        }
    }
}
