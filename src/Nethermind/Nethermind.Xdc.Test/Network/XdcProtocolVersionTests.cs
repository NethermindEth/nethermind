// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.P2P.Messages;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Network;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcProtocolVersionTests
{
    [TestCase(XdcProtocolVersions.Legacy, "xdpos2", XdcProtocolVersions.LegacyMessageIdSpaceSize)]
    [TestCase(XdcProtocolVersions.Xdc164, "xdc164", XdcProtocolVersions.LegacyMessageIdSpaceSize)]
    [TestCase(XdcProtocolVersions.Xdc165, "xdc165", XdcProtocolVersions.MessageIdSpaceSize)]
    public void Version_reserves_the_message_id_space_it_uses(byte version, string name, int messageIdSpaceSize)
    {
        using Peer peer = Peer.Create(version);

        Assert.That(peer.Handler.ProtocolVersion, Is.EqualTo(version));
        Assert.That(peer.Handler.Name, Is.EqualTo(name));
        Assert.That(peer.Handler.MessageIdSpaceSize, Is.EqualTo(messageIdSpaceSize));
    }

    [TestCase(XdcProtocolVersions.Legacy)]
    [TestCase(XdcProtocolVersions.Xdc164)]
    [TestCase(XdcProtocolVersions.Xdc165)]
    public void Order_and_lending_broadcasts_do_not_break_the_session(byte version)
    {
        using Peer peer = Peer.Create(version);
        peer.ReceiveStatus();

        peer.Receive(XdcMessageCode.OrderTx);
        peer.Receive(XdcMessageCode.LendingTx);

        peer.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [TestCase(XdcProtocolVersions.Legacy)]
    [TestCase(XdcProtocolVersions.Xdc164)]
    [TestCase(XdcProtocolVersions.Xdc165)]
    public void Consensus_messages_are_handled_on_every_version(byte version)
    {
        using Peer peer = Peer.Create(version);
        peer.ReceiveStatus();

        Vote vote = new(new BlockRoundInfo(TestItem.KeccakA, 5, 100), 0, new Signature(new byte[64], 0));
        peer.Serializer.Deserialize<VoteMsg>(Arg.Any<IByteBuffer>()).Returns(new VoteMsg { Vote = vote });

        peer.Receive(XdcMessageCode.VoteMsg);

        peer.VotesManager.Received(1).OnReceiveVote(vote);
    }

    [TestCase(XdcProtocolVersions.Legacy)]
    [TestCase(XdcProtocolVersions.Xdc164)]
    public void Pooled_transaction_codes_are_rejected_below_xdc165(byte version)
    {
        using Peer peer = Peer.Create(version);
        peer.ReceiveStatus();

        peer.Receive(XdcMessageCode.NewPooledTransactionHashes);

        peer.Session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }

    [Test]
    public void Xdc165_requests_announced_transactions_on_the_relocated_code()
    {
        using Peer peer = Peer.Create(XdcProtocolVersions.Xdc165);
        peer.ReceiveStatus();
        peer.TxPool.NotifyAboutTx(Arg.Any<Hash256>(), Arg.Any<IMessageHandler<PooledTransactionRequestMessage>>())
            .Returns(AnnounceResult.RequestRequired);
        peer.Serializer.Deserialize<NewPooledTransactionHashesMessage>(Arg.Any<IByteBuffer>())
            .Returns(new NewPooledTransactionHashesMessage(new[] { TestItem.KeccakA }.ToPooledList()));

        peer.Receive(XdcMessageCode.NewPooledTransactionHashes);

        peer.Session.Received(1).DeliverMessage(Arg.Is<P2PMessage>(m =>
            m is XdcGetPooledTransactionsMessage && m.PacketType == XdcMessageCode.GetPooledTransactions));
    }

    [Test]
    public void Xdc165_rejects_the_upstream_pooled_transactions_code()
    {
        using Peer peer = Peer.Create(XdcProtocolVersions.Xdc165);
        peer.ReceiveStatus();

        // 0x0a is unassigned in XDC - reading it as a response would accept transactions nobody sent.
        peer.Receive(Eth65MessageCode.PooledTransactions);

        peer.Session.Received(1).InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }

    private sealed class Peer : IDisposable
    {
        private Peer(ZeroProtocolHandlerBase handler, ISession session, IMessageSerializationService serializer,
            IVotesManager votesManager, ITxPool txPool)
        {
            Handler = handler;
            Session = session;
            Serializer = serializer;
            VotesManager = votesManager;
            TxPool = txPool;
        }

        public ZeroProtocolHandlerBase Handler { get; }
        public ISession Session { get; }
        public IMessageSerializationService Serializer { get; }
        public IVotesManager VotesManager { get; }
        public ITxPool TxPool { get; }

        public static Peer Create(byte version)
        {
            IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
            ISession session = Substitute.For<ISession>();
            session.RemoteNodeId.Returns(TestItem.PublicKeyA);
            session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

            INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();
            nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(Substitute.For<INodeStats>());

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockHeader head = Build.A.BlockHeader.WithNumber(100).TestObject;
            blockTree.Head.Returns(Build.A.Block.WithHeader(head).TestObject);
            blockTree.FindBestSuggestedHeader().Returns(head);

            ISyncServer syncServer = Substitute.For<ISyncServer>();
            syncServer.Head.Returns(head);

            IVotesManager votesManager = Substitute.For<IVotesManager>();
            ITxPool txPool = Substitute.For<ITxPool>();
            ITxGossipPolicy txGossipPolicy = Substitute.For<ITxGossipPolicy>();
            txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
            IForkInfo forkInfo = Substitute.For<IForkInfo>();

            ZeroProtocolHandlerBase handler = version switch
            {
                XdcProtocolVersions.Legacy => new XdcProtocolHandler(
                    Substitute.For<ITimeoutCertificateManager>(), votesManager, Substitute.For<ISyncInfoManager>(),
                    blockTree, session, serializer, nodeStatsManager, syncServer,
                    Substitute.For<IBackgroundTaskScheduler>(), txPool, Substitute.For<IGossipPolicy>(),
                    LimboLogs.Instance, txGossipPolicy),
                XdcProtocolVersions.Xdc164 => new Xdc164ProtocolHandler(
                    Substitute.For<ITimeoutCertificateManager>(), votesManager, Substitute.For<ISyncInfoManager>(),
                    blockTree, session, serializer, nodeStatsManager, syncServer,
                    Substitute.For<IBackgroundTaskScheduler>(), txPool, Substitute.For<IGossipPolicy>(), forkInfo,
                    LimboLogs.Instance, txGossipPolicy),
                XdcProtocolVersions.Xdc165 => new Xdc165ProtocolHandler(
                    Substitute.For<ITimeoutCertificateManager>(), votesManager, Substitute.For<ISyncInfoManager>(),
                    blockTree, session, serializer, nodeStatsManager, syncServer,
                    Substitute.For<IBackgroundTaskScheduler>(), txPool, Substitute.For<IGossipPolicy>(), forkInfo,
                    LimboLogs.Instance, txGossipPolicy),
                _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown XDC protocol version")
            };

            return new Peer(handler, session, serializer, votesManager, txPool);
        }

        public void ReceiveStatus()
        {
            Serializer.Deserialize<StatusMessage>(Arg.Any<IByteBuffer>()).Returns(new StatusMessage());
            Receive(Eth62MessageCode.Status);
        }

        public void Receive(int packetType)
        {
            ZeroPacket packet = new(Unpooled.Buffer()) { PacketType = (byte)packetType };
            Handler.HandleMessage(packet);
        }

        public void Dispose() => Handler.Dispose();
    }
}
