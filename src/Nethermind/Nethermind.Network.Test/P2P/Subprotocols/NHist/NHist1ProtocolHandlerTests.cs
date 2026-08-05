// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.NHist;
using Nethermind.Network.P2P.Subprotocols.NHist.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NHist;

public class NHist1ProtocolHandlerTests
{
    [Test]
    public void GetHistoryRangeAtHeight_forwards_requested_byte_budget_to_history_server()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetHistoryRangeAtHeight(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256>(), Arg.Any<ulong>(), Arg.Any<byte[]?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((ArrayPoolList<HistoryRangeEntry>.Empty(), (byte[]?)null));

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRangeAtHeightMessageSerializer()),
            SerializerInfo.Create(new HistoryRangeAtHeightMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer);

        using GetHistoryRangeAtHeightMessage request = new()
        {
            RequestId = 1,
            StartKey = ValueKeccak.Zero,
            EndKey = ValueKeccak.MaxValue,
            Height = 42,
            Cursor = [],
            ResponseBytes = 1234
        };

        Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRangeAtHeight);

        historyServer.Received(1).GetHistoryRangeAtHeight(request.StartKey, request.EndKey, request.Height, Arg.Any<byte[]?>(), request.ResponseBytes, Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetChangesets_forwards_block_range_to_history_server()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetChangesets(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EmptyChangesets());

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetChangesetsMessageSerializer()),
            SerializerInfo.Create(new ChangesetsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer);

        using GetChangesetsMessage request = new() { RequestId = 1, FromBlock = 10, ToBlock = 20, ResponseBytes = 5555 };

        Handle(handler, serializer, request, NHist1MessageCode.GetChangesets);

        historyServer.Received(1).GetChangesets(request.FromBlock, request.ToBlock, request.ResponseBytes, Arg.Any<CancellationToken>());
    }

    [Test]
    public void ShouldServeNHist_WhenInFlightRequestsExceedQuota_DisconnectsSessionWithoutRejectingWithinQuota()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRangeAtHeightMessageSerializer()),
            SerializerInfo.Create(new HistoryRangeAtHeightMessageSerializer()));

        QueueingScheduler scheduler = new();
        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            scheduler,
            LimboLogs.Instance,
            historyServer);

        for (int i = 0; i < 4; i++)
        {
            using GetHistoryRangeAtHeightMessage request = new() { RequestId = i, Cursor = [] };
            Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRangeAtHeight);
        }

        session.DidNotReceive().InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());

        using GetHistoryRangeAtHeightMessage overQuota = new() { RequestId = 4, Cursor = [] };
        Handle(handler, serializer, overQuota, NHist1MessageCode.GetHistoryRangeAtHeight);

        session.Received(1).InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());
        Assert.That(scheduler.ScheduledCount, Is.EqualTo(4), "the request that broke the in-flight quota must never reach the background scheduler at all");
    }

    private sealed class QueueingScheduler : IBackgroundTaskScheduler
    {
        public int ScheduledCount { get; private set; }

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null, string? source = null)
        {
            ScheduledCount++;
            return true;
        }
    }

    [Test]
    public void ShouldServeNHist_WhenHistoryServerCannotServe_DisconnectsSession()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(false);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRangeAtHeightMessageSerializer()),
            SerializerInfo.Create(new HistoryRangeAtHeightMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer);

        using GetHistoryRangeAtHeightMessage request = new() { RequestId = 1, Cursor = [] };

        Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRangeAtHeight);

        session.Received(1).InitiateDisconnect(DisconnectReason.NHistServerNotImplemented, Arg.Any<string>());
    }

    private static async IAsyncEnumerable<ChangesetChunkEntry> EmptyChangesets()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static void Handle<TMessage>(NHist1ProtocolHandler handler, IMessageSerializationService serializer, TMessage request, byte packetType)
        where TMessage : MessageBase
    {
        IByteBuffer? buffer = serializer.ZeroSerialize(request);
        try
        {
            buffer.ReadByte();
            ZeroPacket packet = new(buffer) { PacketType = packetType };
            buffer = null;
            try
            {
                handler.HandleMessage(packet);
            }
            finally
            {
                ReferenceCountUtil.Release(packet);
            }
        }
        finally
        {
            buffer?.SafeRelease();
        }
    }
}
