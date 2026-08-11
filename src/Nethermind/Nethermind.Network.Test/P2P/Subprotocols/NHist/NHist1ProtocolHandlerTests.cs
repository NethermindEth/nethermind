// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Blockchain.Synchronization;
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
            historyServer,
            new SyncConfig());

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
            historyServer,
            new SyncConfig());

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
            historyServer,
            new SyncConfig());

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
            historyServer,
            new SyncConfig());

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
            historyServer,
            new SyncConfig());

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
    public void GetHistoryRows_WhenSchedulerQueueIsFull_SendsRefusedResponseInsteadOfSilence()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRowsMessageSerializer()),
            SerializerInfo.Create(new HistoryRowsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            new RejectingScheduler(),
            LimboLogs.Instance,
            historyServer,
            new SyncConfig());

        using GetHistoryRowsMessage request = new()
        {
            RequestId = 77,
            Column = HistoryRowColumn.Code,
            StartKey = [0],
            EndKey = [0xFF],
            Cursor = [],
            ResponseBytes = 4321
        };

        Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRows);

        session.Received(1).DeliverMessage(Arg.Is<HistoryRowsMessage>(m => m.RequestId == 77 && m.Refused && m.Entries.Count == 0));
        session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public void GetHistoryRows_WhenInFlightQuotaIsExceeded_SendsRefusedResponseInsteadOfDisconnecting()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRowsMessageSerializer()),
            SerializerInfo.Create(new HistoryRowsMessageSerializer()));

        QueueingScheduler scheduler = new();
        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            scheduler,
            LimboLogs.Instance,
            historyServer,
            new SyncConfig());

        for (int i = 0; i < IHistoryServer.MaxInFlightRequestsPerPeer; i++)
        {
            using GetHistoryRowsMessage request = new() { RequestId = i, Column = HistoryRowColumn.Code, StartKey = [0], EndKey = [0xFF], Cursor = [] };
            Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRows);
        }

        using GetHistoryRowsMessage overQuota = new() { RequestId = 99, Column = HistoryRowColumn.Code, StartKey = [0], EndKey = [0xFF], Cursor = [] };
        Handle(handler, serializer, overQuota, NHist1MessageCode.GetHistoryRows);

        session.Received(1).DeliverMessage(Arg.Is<HistoryRowsMessage>(m => m.RequestId == 99 && m.Refused));
        session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(scheduler.ScheduledCount, Is.EqualTo(IHistoryServer.MaxInFlightRequestsPerPeer),
            "the over-quota rows request must be refused without reaching the background scheduler");
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
            historyServer,
            new SyncConfig());

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
    public void Init_SendsStatusMessageWithSupportsFullCloneAndRowFormatVersion()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.CanServeFullClone.Returns(true);
        historyServer.RowFormatVersion.Returns((byte)3);

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IMessageSerializationService>(),
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer,
            new SyncConfig());

        handler.Init();

        session.Received(1).DeliverMessage(Arg.Is<NHistStatusMessage>(m => m.SupportsFullClone && m.RowFormatVersion == 3));
    }

    [Test]
    public void GetHistoryRows_forwards_request_to_history_server()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetHistoryRows(Arg.Any<HistoryRowColumn>(), Arg.Any<byte[]>(), Arg.Any<byte[]>(), Arg.Any<byte[]?>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ArrayPoolList<HistoryRowEntry>.Empty(), (byte[]?)null, false));

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRowsMessageSerializer()),
            SerializerInfo.Create(new HistoryRowsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer,
            new SyncConfig());

        using GetHistoryRowsMessage request = new()
        {
            RequestId = 1,
            Column = HistoryRowColumn.Code,
            StartKey = [0],
            EndKey = [0xFF],
            Cursor = [],
            ResponseBytes = 4321
        };

        Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRows);

        historyServer.Received(1).GetHistoryRows(
            HistoryRowColumn.Code,
            Arg.Is<byte[]>(b => b.SequenceEqual(request.StartKey)),
            Arg.Is<byte[]>(b => b.SequenceEqual(request.EndKey)),
            Arg.Any<byte[]?>(),
            request.ResponseBytes,
            NHistMessageLimits.MaxResponseRowEntries,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetHistoryRows_WhenServerRefuses_ResponseCarriesRefusedTrue()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        historyServer.CanServe.Returns(true);
        historyServer.GetHistoryRows(Arg.Any<HistoryRowColumn>(), Arg.Any<byte[]>(), Arg.Any<byte[]>(), Arg.Any<byte[]?>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ArrayPoolList<HistoryRowEntry>.Empty(), (byte[]?)null, true));

        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        HistoryRowsMessage? captured = null;
        session.When(s => s.DeliverMessage(Arg.Any<HistoryRowsMessage>())).Do(call => captured = call.Arg<HistoryRowsMessage>());

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRowsMessageSerializer()),
            SerializerInfo.Create(new HistoryRowsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session,
            Substitute.For<INodeStatsManager>(),
            serializer,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            historyServer,
            new SyncConfig());

        using GetHistoryRowsMessage request = new() { RequestId = 1, Column = HistoryRowColumn.AccountHistory, StartKey = [0], EndKey = [0xFF], Cursor = [] };

        Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRows);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Refused, Is.True, "a windowed-source refusal must reach the wire as Refused=true, not an ambiguous empty result");
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
            historyServer,
            new SyncConfig());

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
            historyServer,
            new SyncConfig());

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
            historyServer,
            new SyncConfig());

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
            historyServer,
            new SyncConfig());

        using GetHistoryRangeAtHeightMessage request = new() { RequestId = 1, Cursor = [] };

        Handle(handler, serializer, request, NHist1MessageCode.GetHistoryRangeAtHeight);

        session.Received(1).InitiateDisconnect(DisconnectReason.NHistServerNotImplemented, Arg.Any<string>());
    }

    [Test]
    public async Task GetChangesets_ClientMethod_RoundTripsThroughHandleMessage()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetChangesetsMessageSerializer()),
            SerializerInfo.Create(new ChangesetsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session, CreateNodeStatsManager(), serializer, RunImmediatelyScheduler.Instance, LimboLogs.Instance, historyServer, new SyncConfig());

        GetChangesetsMessage? sent = null;
        session.When(s => s.DeliverMessage(Arg.Any<GetChangesetsMessage>())).Do(call => sent = call.Arg<GetChangesetsMessage>());

        Task<ChangesetsMessage> clientTask = handler.GetChangesets(10, 20, CancellationToken.None);

        Assert.That(sent, Is.Not.Null, "the client method must send a GetChangesetsMessage over the session before its task can ever complete");

        using ChangesetsMessage response = new()
        {
            RequestId = sent!.RequestId,
            Chunks = new ArrayPoolList<ChangesetChunkEntry>(1) { new(10, 0, true, new byte[] { 1, 2, 3 }) }
        };
        Handle(handler, serializer, response, NHist1MessageCode.Changesets);

        ChangesetsMessage result = await clientTask;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Chunks.Count, Is.EqualTo(1));
            Assert.That(result.Chunks[0].Block, Is.EqualTo(10UL));
            Assert.That(result.Chunks[0].Payload.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }

    [Test]
    public async Task GetHistoryRows_ClientMethod_RoundTripsThroughHandleMessage()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRowsMessageSerializer()),
            SerializerInfo.Create(new HistoryRowsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session, CreateNodeStatsManager(), serializer, RunImmediatelyScheduler.Instance, LimboLogs.Instance, historyServer, new SyncConfig());

        GetHistoryRowsMessage? sent = null;
        session.When(s => s.DeliverMessage(Arg.Any<GetHistoryRowsMessage>())).Do(call => sent = call.Arg<GetHistoryRowsMessage>());

        Task<HistoryRowsMessage> clientTask = handler.GetHistoryRows(HistoryRowColumn.AccountHistory, [1, 2], [3, 4], cursor: null, CancellationToken.None);

        Assert.That(sent, Is.Not.Null);

        using HistoryRowsMessage response = new()
        {
            RequestId = sent!.RequestId,
            Refused = false,
            Entries = new ArrayPoolList<HistoryRowEntry>(1) { new(new byte[] { 9, 9 }, new byte[] { 7 }) },
            NextCursor = [5, 5]
        };
        Handle(handler, serializer, response, NHist1MessageCode.HistoryRows);

        HistoryRowsMessage result = await clientTask;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Refused, Is.False);
            Assert.That(result.Entries.Count, Is.EqualTo(1));
            Assert.That(result.Entries[0].Key, Is.EqualTo(new byte[] { 9, 9 }));
            Assert.That(result.NextCursor, Is.EqualTo(new byte[] { 5, 5 }));
        }
    }

    [Test]
    public async Task INHistSyncPeer_GetChangesets_CopiesEntriesAndDisposesTheWireMessage()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetChangesetsMessageSerializer()),
            SerializerInfo.Create(new ChangesetsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session, CreateNodeStatsManager(), serializer, RunImmediatelyScheduler.Instance, LimboLogs.Instance, historyServer, new SyncConfig());

        INHistSyncPeer syncPeer = handler;

        GetChangesetsMessage? sent = null;
        session.When(s => s.DeliverMessage(Arg.Any<GetChangesetsMessage>())).Do(call => sent = call.Arg<GetChangesetsMessage>());

        Task<NHistChangesetsPage> clientTask = syncPeer.GetChangesets(1, 5, CancellationToken.None);
        Assert.That(sent, Is.Not.Null);

        ArrayPoolList<ChangesetChunkEntry> chunks = new(1) { new(1, 0, true, new byte[] { 4, 5 }) };
        using ChangesetsMessage response = new() { RequestId = sent!.RequestId, Chunks = chunks };
        Handle(handler, serializer, response, NHist1MessageCode.Changesets);

        NHistChangesetsPage page = await clientTask;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Chunks.Count, Is.EqualTo(1));
            Assert.That(page.Chunks[0].Payload.ToArray(), Is.EqualTo(new byte[] { 4, 5 }),
                "the plain-DTO surface must carry a copy of the payload, independent of the pooled wire message's lifetime");
        }
    }

    [Test]
    public async Task INHistSyncPeer_GetHistoryRows_PreservesRefusedAndCursor()
    {
        IHistoryServer historyServer = Substitute.For<IHistoryServer>();
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        IMessageSerializationService serializer = new MessageSerializationService(
            SerializerInfo.Create(new GetHistoryRowsMessageSerializer()),
            SerializerInfo.Create(new HistoryRowsMessageSerializer()));

        NHist1ProtocolHandler handler = new(
            session, CreateNodeStatsManager(), serializer, RunImmediatelyScheduler.Instance, LimboLogs.Instance, historyServer, new SyncConfig());

        INHistSyncPeer syncPeer = handler;

        GetHistoryRowsMessage? sent = null;
        session.When(s => s.DeliverMessage(Arg.Any<GetHistoryRowsMessage>())).Do(call => sent = call.Arg<GetHistoryRowsMessage>());

        Task<NHistRowsPage> clientTask = syncPeer.GetHistoryRows(HistoryRowColumn.StorageHistory, [1], [2], cursor: null, CancellationToken.None);
        Assert.That(sent, Is.Not.Null);

        using HistoryRowsMessage response = new()
        {
            RequestId = sent!.RequestId,
            Refused = true,
            Entries = ArrayPoolList<HistoryRowEntry>.Empty(),
            NextCursor = null
        };
        Handle(handler, serializer, response, NHist1MessageCode.HistoryRows);

        NHistRowsPage page = await clientTask;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Refused, Is.True, "a peer's refusal must survive the INHistSyncPeer translation, not just the raw wire message");
            Assert.That(page.Entries, Is.Empty);
            Assert.That(page.NextCursor, Is.Null);
        }
    }

    // ZeroProtocolHandlerBase resolves _nodeStats = nodeStats.GetOrAdd(session.Node) at construction time; a bare
    // Substitute.For<INodeStatsManager>() leaves that null, which only matters for the client-side request
    // methods (RunLatencyRequestSizer) this file did not exercise until the round-trip tests below - a real
    // NodeStatsLight is required for those to actually invoke the request and complete.
    private static INodeStatsManager CreateNodeStatsManager()
    {
        INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();
        nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(call => new NodeStatsLight(call.Arg<Node>()));
        return nodeStatsManager;
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
