// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test.P2P;

[TestFixture]
public class Xdpos2ProtocolHandlerTests
{
    private static (Xdpos2ProtocolHandler handler, IMessageSerializationService serializer, ISession session,
        IVotesManager votesManager, ITimeoutCertificateManager timeoutManager, ISyncInfoManager syncInfoManager)
        CreateAll()
    {
        IVotesManager votesManager = Substitute.For<IVotesManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ISyncInfoManager syncInfoManager = Substitute.For<ISyncInfoManager>();
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        ISession session = Substitute.For<ISession>();
        session.RemoteNodeId.Returns(TestItem.PublicKeyA);
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();
        nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(Substitute.For<INodeStats>());

        Xdpos2ProtocolHandler handler = new(
            timeoutManager,
            votesManager,
            syncInfoManager,
            session,
            serializer,
            nodeStatsManager,
            Substitute.For<ISyncServer>(),
            Substitute.For<IBackgroundTaskScheduler>(),
            Substitute.For<ITxPool>(),
            Substitute.For<IGossipPolicy>(),
            Substitute.For<IForkInfo>(),
            LimboLogs.Instance,
            Substitute.For<ITxGossipPolicy>());

        return (handler, serializer, session, votesManager, timeoutManager, syncInfoManager);
    }

    private static ZeroPacket CreatePacket(int packetType)
    {
        IByteBuffer buffer = Unpooled.Buffer();
        ZeroPacket packet = new(buffer);
        packet.PacketType = (byte)packetType;
        return packet;
    }

    private static Vote CreateVote(ulong round = 1)
    {
        BlockRoundInfo blockInfo = new(TestItem.KeccakA, round, 100);
        return new Vote(blockInfo, 0, new Signature(new byte[64], 0));
    }

    private static Timeout CreateTimeout(ulong round = 1)
        => new(round, new Signature(new byte[64], 0), 0);

    private static SyncInfo CreateSyncInfo(ulong qcRound = 1)
    {
        BlockRoundInfo blockInfo = new(TestItem.KeccakA, qcRound, 100);
        QuorumCertificate qc = new(blockInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(1, Array.Empty<Signature>(), 0);
        return new SyncInfo(qc, tc);
    }


    [Test]
    public void HandleMessage_VoteMsg_RoutesToVotesManager()
    {
        (Xdpos2ProtocolHandler handler, IMessageSerializationService serializer, _,
            IVotesManager votesManager, _, _) = CreateAll();
        using (handler)
        {
            Vote vote = CreateVote(round: 5);
            ZeroPacket packet = CreatePacket(Xdpos2MessageCode.VoteMsg);
            serializer.Deserialize<VoteMsg>(packet.Content).Returns(new VoteMsg { Vote = vote });

            handler.HandleMessage(packet);

            votesManager.Received(1).OnReceiveVote(vote);
        }
    }

    [Test]
    public void HandleMessage_TimeoutMsg_RoutesToTimeoutManager()
    {
        (Xdpos2ProtocolHandler handler, IMessageSerializationService serializer, _,
            _, ITimeoutCertificateManager timeoutManager, _) = CreateAll();
        using (handler)
        {
            Timeout timeout = CreateTimeout(round: 3);
            ZeroPacket packet = CreatePacket(Xdpos2MessageCode.TimeoutMsg);
            serializer.Deserialize<TimeoutMsg>(packet.Content).Returns(new TimeoutMsg { Timeout = timeout });

            handler.HandleMessage(packet);

            timeoutManager.Received(1).OnReceiveTimeout(Arg.Any<Timeout>());
        }
    }

    [Test]
    public void HandleMessage_SyncInfoMsg_WhenVerificationSucceeds_CallsProcessSyncInfo()
    {
        (Xdpos2ProtocolHandler handler, IMessageSerializationService serializer, _,
            _, _, ISyncInfoManager syncInfoManager) = CreateAll();
        using (handler)
        {
            SyncInfo syncInfo = CreateSyncInfo(qcRound: 10);
            ZeroPacket packet = CreatePacket(Xdpos2MessageCode.SyncInfoMsg);
            serializer.Deserialize<SyncInfoMsg>(packet.Content).Returns(new SyncInfoMsg { SyncInfo = syncInfo });
            syncInfoManager.VerifySyncInfo(syncInfo, out Arg.Any<string>()).Returns(true);

            handler.HandleMessage(packet);

            syncInfoManager.Received(1).ProcessSyncInfo(syncInfo);
        }
    }

    [Test]
    public void HandleMessage_SyncInfoMsg_WhenVerificationFails_DoesNotCallProcessSyncInfo()
    {
        (Xdpos2ProtocolHandler handler, IMessageSerializationService serializer, _,
            _, _, ISyncInfoManager syncInfoManager) = CreateAll();
        using (handler)
        {
            SyncInfo syncInfo = CreateSyncInfo(qcRound: 10);
            ZeroPacket packet = CreatePacket(Xdpos2MessageCode.SyncInfoMsg);
            serializer.Deserialize<SyncInfoMsg>(packet.Content).Returns(new SyncInfoMsg { SyncInfo = syncInfo });
            syncInfoManager.VerifySyncInfo(syncInfo, out Arg.Any<string>())
                .Returns(x => { x[1] = "rounds too low"; return false; });

            handler.HandleMessage(packet);

            syncInfoManager.DidNotReceive().ProcessSyncInfo(Arg.Any<SyncInfo>());
        }
    }

    [Test]
    public void SendVote_FirstVote_IsDelivered()
    {
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            Vote vote = CreateVote(round: 1);

            handler.SendVote(vote);

            session.Received(1).DeliverMessage(Arg.Any<VoteMsg>());
        }
    }

    [Test]
    public void SendVote_SameVoteSentTwice_IsDeliveredOnlyOnce()
    {
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            Vote vote = CreateVote(round: 7);

            handler.SendVote(vote);
            handler.SendVote(vote);

            session.Received(1).DeliverMessage(Arg.Any<VoteMsg>());
        }
    }

    [Test]
    public void SendVote_TwoDifferentVotes_BothDelivered()
    {
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            Vote vote1 = CreateVote(round: 1);
            Vote vote2 = CreateVote(round: 2);

            handler.SendVote(vote1);
            handler.SendVote(vote2);

            session.Received(2).DeliverMessage(Arg.Any<VoteMsg>());
        }
    }

    [Test]
    public void SendTimeout_FirstTimeout_IsDelivered()
    {
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            Timeout timeout = CreateTimeout(round: 1);

            handler.SendTimeout(timeout);

            session.Received(1).DeliverMessage(Arg.Any<TimeoutMsg>());
        }
    }

    [Test]
    public void SendTimeout_SameTimeoutSentTwice_IsDeliveredOnlyOnce()
    {
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            Timeout timeout = CreateTimeout(round: 4);

            handler.SendTimeout(timeout);
            handler.SendTimeout(timeout);

            session.Received(1).DeliverMessage(Arg.Any<TimeoutMsg>());
        }
    }

    [Test]
    public void SendTimeout_TwoDifferentTimeouts_BothDelivered()
    {
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            Timeout timeout1 = CreateTimeout(round: 1);
            Timeout timeout2 = CreateTimeout(round: 2);

            handler.SendTimeout(timeout1);
            handler.SendTimeout(timeout2);

            session.Received(2).DeliverMessage(Arg.Any<TimeoutMsg>());
        }
    }

    [Test]
    public void SendSyncinfo_IsDelivered()
    {
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            SyncInfo syncInfo = CreateSyncInfo(qcRound: 5);

            handler.SendSyncinfo(syncInfo);

            session.Received(1).DeliverMessage(Arg.Any<SyncInfoMsg>());
        }
    }

    [Test]
    public void SendSyncinfo_SameSyncInfoTwice_IsDeliveredTwice()
    {
        // SyncInfo has no deduplication cache; each call should send
        (Xdpos2ProtocolHandler handler, _, ISession session, _, _, _) = CreateAll();
        using (handler)
        {
            SyncInfo syncInfo = CreateSyncInfo(qcRound: 5);

            handler.SendSyncinfo(syncInfo);
            handler.SendSyncinfo(syncInfo);

            session.Received(2).DeliverMessage(Arg.Any<SyncInfoMsg>());
        }
    }
}
