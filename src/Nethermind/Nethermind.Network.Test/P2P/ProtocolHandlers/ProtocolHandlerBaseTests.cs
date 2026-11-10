// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.Test.P2P.Subprotocols;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.ProtocolHandlers;

[Parallelizable(ParallelScope.Self)]
public class ProtocolHandlerBaseTests
{
    private static readonly Func<TestRequestMessage, CancellationToken, Task<TestResponseMessage>> SyncServeTaskHandler =
        static (r, _) => Task.FromResult(r.Serve());

    private static readonly Func<TestRequestMessage, CancellationToken, ValueTask<TestResponseMessage>> SyncServeValueTaskHandler =
        static (r, _) => ValueTask.FromResult(r.Serve());

    private class TestProtocolHandler(
        ISession session,
        TimeSpan initTimeout,
        IBackgroundTaskScheduler? backgroundTaskScheduler = null,
        IMessageSerializationService? serializationService = null,
        ILogManager? logManager = null)
        : ProtocolHandlerBase(session, Substitute.For<INodeStatsManager>(), serializationService ?? Substitute.For<IMessageSerializationService>(), backgroundTaskScheduler ?? Substitute.For<IBackgroundTaskScheduler>(), logManager ?? LimboLogs.Instance)
    {
        public override string Name => "test";
        protected override TimeSpan InitTimeout => initTimeout;
        public override byte ProtocolVersion => 1;
        public override string ProtocolCode => "test";
        public override int MessageIdSpaceSize => 0;
        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized = delegate { };
        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested = delegate { };
        public Task StartTimeoutCheck() => CheckProtocolInitTimeout();
        public void SimulateLateInitMessage() => ReceivedProtocolInitMsg(new AckMessage());
        public void ScheduleBackgroundTask(Func<int, CancellationToken, ValueTask> backgroundTask) =>
            BackgroundTaskScheduler.TryScheduleBackgroundTask(1, backgroundTask);
        public void ScheduleBackgroundTaskFor(TestRequestMessage request) =>
            BackgroundTaskScheduler.TryScheduleBackgroundTask(request, static (_, _) => ValueTask.CompletedTask);
        public void ScheduleSyncServeTask(TestRequestMessage request, Func<TestRequestMessage, CancellationToken, Task<TestResponseMessage>> syncServe) =>
            BackgroundTaskScheduler.TryScheduleSyncServe(request, syncServe);
        public void ScheduleSyncServeValueTask(TestRequestMessage request, Func<TestRequestMessage, CancellationToken, ValueTask<TestResponseMessage>> syncServe) =>
            BackgroundTaskScheduler.TryScheduleSyncServe(request, syncServe);
        public void ScheduleHandlerSyncServeTask(TestRequestMessage request) =>
            BackgroundTaskScheduler.TryScheduleSyncServe<TestProtocolHandler, TestRequestMessage, TestResponseMessage, TestSyncServeRequestHandler>(this, request);
        public void ScheduleTransactionServe(GetPooledTransactionsMessage request) =>
            BackgroundTaskScheduler.TryScheduleSyncServe(request, static (_, _) => ValueTask.FromResult(new TestResponseMessage()));
        public TestRequestMessage Deserialize(byte[] data) => Deserialize<TestRequestMessage>(data);
        public void HandleProjectionFailure(MessageDictionary<ProjectionRequestMessage, int> requests, IByteBuffer content) =>
            HandleCorrelatedResponse<ProjectionRequestMessage, ProjectionResponseMessage, int>(
                requests,
                content,
                content.ReadableBytes,
                static (_, _) => throw new InvalidOperationException("projection failed"));
        public override void Init() { }
        public override void Dispose() { }
        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) { }
        public override void HandleMessage(Packet msg) { }
    }

    private class ImmediateBackgroundTaskScheduler(CancellationToken cancellationToken) : IBackgroundTaskScheduler
    {
        public Task ScheduledTask { get; private set; } = Task.CompletedTask;

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            ScheduledTask = fulfillFunc(request, cancellationToken);
            return true;
        }
    }

    /// <summary>Keeps each scheduled task until the test runs it.</summary>
    private sealed class QueuingBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        private readonly Queue<Func<Task>> _tasks = new();

        public int Count => _tasks.Count;

        public bool Accept { get; set; } = true;

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            if (!Accept) return false;
            _tasks.Enqueue(() => fulfillFunc(request, CancellationToken.None));
            return true;
        }

        public Task RunNext() => _tasks.Dequeue()();
    }

    private readonly struct TestSyncServeRequestHandler : ISyncServeRequestHandler<TestProtocolHandler, TestRequestMessage, TestResponseMessage>
    {
        public static Task<TestResponseMessage> Execute(TestProtocolHandler handler, TestRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Serve());
    }

    /// <summary>
    /// Records the runner delegate the most recently scheduled task was dispatched with.
    /// </summary>
    /// <remarks>
    /// A cached runner is handed to the scheduler by reference, so repeated scheduling yielding the same
    /// instance is what proves no per-call wrapper delegate is allocated.
    /// </remarks>
    private sealed class RunnerCapturingBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        public Delegate? CapturedRunner { get; private set; }

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            CapturedRunner = fulfillFunc;
            return true;
        }
    }

    /// <summary>Records the name each scheduled task is reported under.</summary>
    private sealed class NameCapturingBackgroundTaskScheduler : IBackgroundTaskScheduler
    {
        public string? ReportedName { get; private set; }

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            ReportedName = BackgroundTaskTypeRegistry.GetName(TReq.TaskId);
            return true;
        }
    }

    private sealed class TestRequestMessage(bool fail = false) : P2PMessage
    {
        public override int PacketType => 1;
        public override string Protocol => "test";
        public bool IsDisposed { get; private set; }

        public bool Served { get; private set; }

        public TestResponseMessage Serve()
        {
            if (fail) throw new InvalidOperationException("serve failed");
            Served = true;
            return new TestResponseMessage();
        }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }
    }

    public enum SchedulingPath
    {
        SyncServeTask,
        SyncServeValueTask,
        HandlerSyncServe,
        BackgroundTask
    }

    /// <remarks>
    /// Each wrapper request is generic over the message it wraps, so its own type name is identical for
    /// every instantiation. The wrappers therefore report the wrapped type instead, which nothing in the
    /// type system enforces — this pins it for every scheduling path.
    /// </remarks>
    [Test]
    public void Scheduling_reports_the_wrapped_message_type([Values] SchedulingPath path)
    {
        NameCapturingBackgroundTaskScheduler scheduler = new();
        TestProtocolHandler handler = new(Substitute.For<ISession>(), TimeSpan.FromSeconds(1), scheduler);

        Schedule(handler, path, new TestRequestMessage());

        Assert.That(scheduler.ReportedName, Is.EqualTo(nameof(TestRequestMessage)));
    }

    private static void Schedule(TestProtocolHandler handler, SchedulingPath path, TestRequestMessage request)
    {
        switch (path)
        {
            case SchedulingPath.SyncServeTask:
                handler.ScheduleSyncServeTask(request, SyncServeTaskHandler);
                break;
            case SchedulingPath.SyncServeValueTask:
                handler.ScheduleSyncServeValueTask(request, SyncServeValueTaskHandler);
                break;
            case SchedulingPath.HandlerSyncServe:
                handler.ScheduleHandlerSyncServeTask(request);
                break;
            case SchedulingPath.BackgroundTask:
                handler.ScheduleBackgroundTaskFor(request);
                break;
        }
    }

    private sealed class TestResponseMessage : P2PMessage
    {
        public override int PacketType => 2;
        public override string Protocol => "test";
    }

    private sealed class ProjectionRequestMessage : Eth66MessageBase
    {
        public ProjectionRequestMessage(long requestId) : base(false) => RequestId = requestId;

        public override int PacketType => 3;
        public override string Protocol => "eth";
        public bool IsDisposed { get; private set; }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }
    }

    private sealed class ProjectionResponseMessage : Eth66MessageBase
    {
        public ProjectionResponseMessage(long requestId) : base(false) => RequestId = requestId;

        public override int PacketType => 4;
        public override string Protocol => "eth";
        public bool IsDisposed { get; private set; }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }
    }

    [Test]
    public async Task Sync_serve_requests_over_the_in_flight_limit_are_dropped(
        [Values(SchedulingPath.SyncServeTask, SchedulingPath.SyncServeValueTask, SchedulingPath.HandlerSyncServe)] SchedulingPath path,
        [Values] bool requestFails)
    {
        const int limit = BackgroundTaskSchedulerWrapper.MaxSyncServeRequestsInFlight;
        QueuingBackgroundTaskScheduler scheduler = new();
        TestProtocolHandler handler = new(Substitute.For<ISession>(), TimeSpan.FromSeconds(1), scheduler);
        long droppedBefore = Metrics.SyncServeRequestsDroppedOverPeerLimit;

        for (int i = 0; i < limit; i++) Schedule(handler, path, new TestRequestMessage(requestFails));
        TestRequestMessage overLimit = new();
        Schedule(handler, path, overLimit);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scheduler.Count, Is.EqualTo(limit));
            Assert.That(overLimit.IsDisposed, Is.True);
            Assert.That(Metrics.SyncServeRequestsDroppedOverPeerLimit, Is.GreaterThan(droppedBefore));
        }

        await scheduler.RunNext();
        Schedule(handler, path, new TestRequestMessage());
        Schedule(handler, path, new TestRequestMessage());
        Assert.That(scheduler.Count, Is.EqualTo(limit));

        Schedule(handler, SchedulingPath.BackgroundTask, new TestRequestMessage());
        Assert.That(scheduler.Count, Is.EqualTo(limit + 1), "Other background tasks are not limited");
    }

    [Test]
    public void Sync_serve_requests_refused_by_the_scheduler_do_not_keep_their_slot(
        [Values(SchedulingPath.SyncServeTask, SchedulingPath.SyncServeValueTask, SchedulingPath.HandlerSyncServe)] SchedulingPath path)
    {
        const int limit = BackgroundTaskSchedulerWrapper.MaxSyncServeRequestsInFlight;
        QueuingBackgroundTaskScheduler scheduler = new() { Accept = false };
        TestProtocolHandler handler = new(Substitute.For<ISession>(), TimeSpan.FromSeconds(1), scheduler);

        for (int i = 0; i < limit; i++) Schedule(handler, path, new TestRequestMessage());

        scheduler.Accept = true;
        for (int i = 0; i < limit; i++) Schedule(handler, path, new TestRequestMessage());
        Assert.That(scheduler.Count, Is.EqualTo(limit));
    }

    [Test]
    public async Task Transaction_requests_have_their_own_in_flight_limit()
    {
        const int limit = BackgroundTaskSchedulerWrapper.MaxSyncServeRequestsInFlight;
        QueuingBackgroundTaskScheduler scheduler = new();
        TestProtocolHandler handler = new(Substitute.For<ISession>(), TimeSpan.FromSeconds(1), scheduler);

        for (int i = 0; i <= limit; i++) handler.ScheduleTransactionServe(new GetPooledTransactionsMessage(new ArrayPoolList<ValueHash256>(0)));
        Assert.That(scheduler.Count, Is.EqualTo(limit));

        for (int i = 0; i < limit; i++) Schedule(handler, SchedulingPath.SyncServeTask, new TestRequestMessage());
        Assert.That(scheduler.Count, Is.EqualTo(2 * limit), "Transaction requests do not use the slots of sync requests");

        await scheduler.RunNext();
        handler.ScheduleTransactionServe(new GetPooledTransactionsMessage(new ArrayPoolList<ValueHash256>(0)));
        Assert.That(scheduler.Count, Is.EqualTo(2 * limit), "A finished transaction request gives back its own slot");
    }

    [Test]
    public async Task Sync_serve_request_is_skipped_when_the_outbound_buffer_is_full(
        [Values(SchedulingPath.SyncServeTask, SchedulingPath.SyncServeValueTask, SchedulingPath.HandlerSyncServe)] SchedulingPath path,
        [Values] bool bufferFull)
    {
        ISession session = Substitute.For<ISession>();
        session.IsOutboundBufferFull.Returns(bufferFull);
        ImmediateBackgroundTaskScheduler scheduler = new(CancellationToken.None);
        TestProtocolHandler handler = new(session, TimeSpan.FromSeconds(1), scheduler);
        TestRequestMessage request = new();

        Schedule(handler, path, request);
        await scheduler.ScheduledTask;

        session.Received(bufferFull ? 0 : 1).DeliverMessage(Arg.Any<TestResponseMessage>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.IsDisposed, Is.EqualTo(bufferFull));
            Assert.That(request.Served, Is.EqualTo(!bufferFull));
        }
    }

    [Test]
    public async Task Skipped_sync_serve_requests_give_back_their_slot(
        [Values(SchedulingPath.SyncServeTask, SchedulingPath.SyncServeValueTask, SchedulingPath.HandlerSyncServe)] SchedulingPath path)
    {
        ISession session = Substitute.For<ISession>();
        session.IsOutboundBufferFull.Returns(true);
        ImmediateBackgroundTaskScheduler scheduler = new(CancellationToken.None);
        TestProtocolHandler handler = new(session, TimeSpan.FromSeconds(1), scheduler);

        for (int i = 0; i < BackgroundTaskSchedulerWrapper.MaxSyncServeRequestsInFlight; i++)
        {
            Schedule(handler, path, new TestRequestMessage());
            await scheduler.ScheduledTask;
        }

        session.IsOutboundBufferFull.Returns(false);
        TestRequestMessage request = new();
        Schedule(handler, path, request);
        await scheduler.ScheduledTask;

        Assert.That(request.Served, Is.True);
    }

    [Test]
    public async Task Late_init_message_after_timeout_does_not_throw()
    {
        ISession session = Substitute.For<ISession>();
        TestProtocolHandler handler = new(session, TimeSpan.FromMilliseconds(50));

        await handler.StartTimeoutCheck();

        Assert.DoesNotThrow(() => handler.SimulateLateInitMessage());
        session.Received().InitiateDisconnect(DisconnectReason.ProtocolInitTimeout, Arg.Any<string>());
    }

    [Test]
    public async Task Operation_canceled_behavior_depends_on_session_closing([Values] bool sessionIsClosing)
    {
        ISession session = Substitute.For<ISession>();
        session.IsClosing.Returns(sessionIsClosing);

        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        ImmediateBackgroundTaskScheduler backgroundTaskScheduler = new(cancellationTokenSource.Token);
        TestProtocolHandler handler = new(session, TimeSpan.FromMilliseconds(50), backgroundTaskScheduler);

        handler.ScheduleBackgroundTask(static (_, cancellationToken) => ValueTask.FromException(new OperationCanceledException(cancellationToken)));

        await backgroundTaskScheduler.ScheduledTask;

        if (sessionIsClosing)
        {
            session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        else
        {
            session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure, Arg.Any<string>());
        }
    }

    [Test]
    public void Scheduling_does_not_allocate_wrapper_delegate([Values] SchedulingPath path)
    {
        RunnerCapturingBackgroundTaskScheduler scheduler = new();
        TestProtocolHandler handler = new(Substitute.For<ISession>(), TimeSpan.FromMilliseconds(50), scheduler);
        TestRequestMessage request = new();

        Schedule(handler, path, request);
        Delegate? firstRunner = scheduler.CapturedRunner;

        Schedule(handler, path, request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRunner, Is.Not.Null);
            Assert.That(scheduler.CapturedRunner, Is.SameAs(firstRunner));
        }
    }

    [Test]
    public void Correlated_response_disposes_decoded_message_when_projection_fails()
    {
        IMessageSerializationService serializationService = Substitute.For<IMessageSerializationService>();
        ProjectionResponseMessage response = new(111);
        serializationService.Deserialize<ProjectionResponseMessage>(Arg.Any<IByteBuffer>()).Returns(callInfo =>
        {
            IByteBuffer content = (IByteBuffer)callInfo[0];
            content.SkipBytes(content.ReadableBytes);
            return response;
        });
        TestProtocolHandler handler = new(
            Substitute.For<ISession>(),
            TimeSpan.FromMilliseconds(50),
            serializationService: serializationService);
        MessageDictionary<ProjectionRequestMessage, int> requests = new(handler);
        Request<ProjectionRequestMessage, int> request = new(new ProjectionRequestMessage(111));
        requests.Send(request);

        using DisposableByteBuffer content = UndecodableResponse.Create(111).AsDisposable();
        Assert.That(() => handler.HandleProjectionFailure(requests, content), Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsDisposed, Is.True);
            Assert.That(request.Message.IsDisposed, Is.True);
            Assert.That(request.CompletionSource.Task.IsCanceled, Is.True);
        }
    }

    [Test]
    public void Rlp_deserialization_exceptions_are_not_logged_at_debug([Values] bool limitExceeded)
    {
        Exception exception = limitExceeded ? new RlpLimitException("limit") : new RlpReaderException("invalid");
        IMessageSerializationService serializationService = Substitute.For<IMessageSerializationService>();
        serializationService.Deserialize<TestRequestMessage>(Arg.Any<ArraySegment<byte>>()).Returns(_ => throw exception);
        TestLogger logger = new() { IsTrace = false };
        TestProtocolHandler handler = new(
            Substitute.For<ISession>(),
            TimeSpan.FromMilliseconds(50),
            serializationService: serializationService,
            logManager: new OneLoggerLogManager(new ILogger(logger)));

        Assert.That(() => handler.Deserialize([]), Throws.TypeOf(exception.GetType()));
        Assert.That(logger.LogList, Is.Empty);
    }
}
