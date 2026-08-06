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
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.NHist;
using Nethermind.Network.P2P.Subprotocols.NHist.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NHist;

public class NHist1ProtocolHandlerTests
{
    [Test]
    public void GetHistoryRangeAtHeight_forwards_requested_byte_budget_to_history_server()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetHistoryRangeAtHeight(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256>(), Arg.Any<ulong>(), Arg.Any<byte[]?>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

        historyServer.Received(1).GetHistoryRangeAtHeight(request.StartKey, request.EndKey, request.Height, Arg.Any<byte[]?>(), request.ResponseBytes, NHistMessageLimits.MaxResponseEntries, Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetHistoryRangeAtHeight_WhenRequestedBytesExceedCap_ClampsToMaxResponseBytesBeforeCallingHistoryServer()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetHistoryRangeAtHeight(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256>(), Arg.Any<ulong>(), Arg.Any<byte[]?>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
            Height = 1,
            Cursor = [],
            ResponseBytes = long.MaxValue
        };

        Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRangeAtHeight);

        historyServer.Received(1).GetHistoryRangeAtHeight(
            request.StartKey, request.EndKey, request.Height, Arg.Any<byte[]?>(), NHistMessageLimits.MaxResponseBytes, NHistMessageLimits.MaxResponseEntries, Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetChangesets_forwards_block_range_to_history_server()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetChangesets(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

        historyServer.Received(1).GetChangesets(request.FromBlock, request.ToBlock, request.ResponseBytes, NHistMessageLimits.MaxResponseChunks, Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetChangesets_WhenRequestedBytesExceedCap_ClampsToMaxResponseBytesBeforeCallingHistoryServer()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetChangesets(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

        using GetChangesetsMessage request = new() { RequestId = 1, FromBlock = 10, ToBlock = 20, ResponseBytes = long.MaxValue };

        Handle(handler, serializer, request, NHist1MessageCode.GetChangesets);

        historyServer.Received(1).GetChangesets(request.FromBlock, request.ToBlock, NHistMessageLimits.MaxResponseBytes, NHistMessageLimits.MaxResponseChunks, Arg.Any<CancellationToken>());
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
    public void Init_SendsStatusMessageWithServedScopesAndNotifiesProtocolInitialized()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 5, 100);
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.ServedScopes.Returns(new[] { scope });

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IMessageSerializationService>(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer);

        bool initialized = false;
        handler.ProtocolInitialized += (_, _) => initialized = true;

        handler.Init();

        using (Assert.EnterMultipleScope())
        {
            session.Received(1).DeliverMessage(Arg.Is<NHistStatusMessage>(m => m.Scopes.Length == 1 && m.Scopes[0].Equals(scope)));
            Assert.That(initialized, Is.True, "Init must notify protocol initialization so the session marks nhist1 usable");
        }
    }

    [Test]
    public void HandleMessage_WhenStatusReceived_StoresPeerServedScopes()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new NHistStatusMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer);

        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 5, 100);
        using NHistStatusMessage statusMessage = new() { Scopes = [scope] };

        Handle(handler, serializer, statusMessage, NHist1MessageCode.Status);

        Assert.That(handler.PeerServedScopes, Is.EqualTo(new[] { scope }), "the peer's advertised served scopes must be recorded exactly as received");
    }

    [Test]
    public void ScheduleOrReleaseQuota_WhenDeserializationFails_ReleasesInFlightQuotaOnEveryFailure()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService realSerializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRangeAtHeightMessageSerializer()),
            SerializerInfo.Create(new HistoryRangeAtHeightMessageSerializer()));

        IMessageSerializationService faultySerializer = Substitute.For<IMessageSerializationService>();
        faultySerializer.Deserialize<GetHistoryRangeAtHeightMessage>(Arg.Any<IByteBuffer>())
            .Throws(new InvalidOperationException("simulated deserialization failure"));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            faultySerializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer);

        for (int i = 0; i < 5; i++)
        {
            using GetHistoryRangeAtHeightMessage request = new() { RequestId = i, Cursor = [] };
            Assert.That(() => Handle(handler, realSerializer, request, NHist1MessageCode.GetHistoryRangeAtHeight), Throws.InvalidOperationException,
                $"request {i} must still surface the deserialization failure to the caller");
        }

        session.DidNotReceive().InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());
    }

    [Test]
    public void ScheduleOrReleaseQuota_WhenBackgroundSchedulerQueueIsFull_ReleasesInFlightQuota()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRangeAtHeightMessageSerializer()),
            SerializerInfo.Create(new HistoryRangeAtHeightMessageSerializer()));

        RejectingScheduler scheduler = new();
        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            scheduler,
            LimboLogs.Instance,
            historyServer);

        for (int i = 0; i < 10; i++)
        {
            using GetHistoryRangeAtHeightMessage request = new() { RequestId = i, Cursor = [] };
            Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRangeAtHeight);
        }

        session.DidNotReceive().InitiateDisconnect(DisconnectReason.MessageLimitsBreached, Arg.Any<string>());
    }

    private sealed class RejectingScheduler : IBackgroundTaskScheduler
    {
        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null, string? source = null) => false;
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
