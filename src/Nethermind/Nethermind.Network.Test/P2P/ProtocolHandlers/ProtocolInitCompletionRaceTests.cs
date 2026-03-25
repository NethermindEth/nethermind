// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

/// <summary>
/// Regression test for the race between TrySetCanceled (timeout path)
/// and SetResult/TrySetResult (late init message) in ProtocolHandlerBase.
///
/// Before fix: TrySetCanceled() on timeout + SetResult() on late message
/// throws InvalidOperationException because the TCS is already completed.
///
/// After fix: TrySetResult() is used instead of SetResult(), making the
/// late message a safe no-op when the TCS is already in a terminal state.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class ProtocolInitCompletionRaceTests
{
    /// <summary>
    /// Minimal concrete subclass of ProtocolHandlerBase with a configurable
    /// init timeout, used to exercise the timeout → late-message race without
    /// needing the full P2P protocol stack.
    /// </summary>
    private class TestProtocolHandler(ISession session, TimeSpan initTimeout)
        : ProtocolHandlerBase(
            session,
            Substitute.For<INodeStatsManager>(),
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<IBackgroundTaskScheduler>(),
            LimboLogs.Instance)
    {
        public override string Name => "test";
        protected override TimeSpan InitTimeout => initTimeout;
        public override byte ProtocolVersion => 1;
        public override string ProtocolCode => "test";
        public override int MessageIdSpaceSize => 0;

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized = delegate { };
        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested = delegate { };

        public Task StartTimeoutCheck() => CheckProtocolInitTimeout();

        /// <summary>
        /// Simulates a late init message arriving after timeout has already fired.
        /// Calls the protected ReceivedProtocolInitMsg which sets the TCS.
        /// </summary>
        public void SimulateLateInitMessage() => ReceivedProtocolInitMsg(new AckMessage());

        public override void Init() { }
        public override void Dispose() { }
        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) { }
        public override void HandleMessage(Packet msg) { }
    }

    [Test]
    public async Task Late_init_message_after_timeout_does_not_throw()
    {
        ISession session = Substitute.For<ISession>();
        TestProtocolHandler handler = new(session, TimeSpan.FromMilliseconds(50));

        // Start the timeout check — it will TrySetCanceled after 50ms
        Task timeoutTask = handler.StartTimeoutCheck();
        await timeoutTask;

        // Timeout has fired, TCS is now in Canceled state.
        // Simulate a late init message arriving after the timeout.
        // Before fix (SetResult): throws InvalidOperationException.
        // After fix (TrySetResult): safe no-op.
        Assert.DoesNotThrow(() => handler.SimulateLateInitMessage());

        session.Received().InitiateDisconnect(
            DisconnectReason.ProtocolInitTimeout,
            Arg.Any<string>());
    }
}
