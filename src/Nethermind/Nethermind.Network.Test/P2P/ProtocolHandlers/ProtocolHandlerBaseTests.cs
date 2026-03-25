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

[Parallelizable(ParallelScope.Self)]
public class ProtocolHandlerBaseTests
{
    private class TestProtocolHandler(ISession session, TimeSpan initTimeout)
        : ProtocolHandlerBase(session, Substitute.For<INodeStatsManager>(), Substitute.For<IMessageSerializationService>(), Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance)
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

        await handler.StartTimeoutCheck();

        Assert.DoesNotThrow(() => handler.SimulateLateInitMessage());
        session.Received().InitiateDisconnect(DisconnectReason.ProtocolInitTimeout, Arg.Any<string>());
    }
}
