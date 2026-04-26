// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.ProtocolHandlers;

[Parallelizable(ParallelScope.Self)]
public class ProtocolHandlerBaseTests
{
    private class TestProtocolHandler(ISession session, TimeSpan initTimeout, IBackgroundTaskScheduler? backgroundTaskScheduler = null)
        : ProtocolHandlerBase(session, Substitute.For<INodeStatsManager>(), Substitute.For<IMessageSerializationService>(), backgroundTaskScheduler ?? Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance)
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
            BackgroundTaskScheduler.TryScheduleBackgroundTask(1, backgroundTask, "test");
        public override void Init() { }
        public override void Dispose() { }
        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) { }
        public override void HandleMessage(Packet msg) { }
    }

    private class ImmediateBackgroundTaskScheduler(CancellationToken cancellationToken) : IBackgroundTaskScheduler
    {
        public Task ScheduledTask { get; private set; } = Task.CompletedTask;

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null, string? source = null)
        {
            ScheduledTask = fulfillFunc(request, cancellationToken);
            return true;
        }
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

    [TestCase(true)]
    [TestCase(false)]
    public async Task Operation_canceled_behavior_depends_on_session_closing(bool sessionIsClosing)
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
}
