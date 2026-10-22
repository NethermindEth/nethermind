// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;

namespace Nethermind.Network.Test;

internal sealed class RecordingProtocolHandler(ISession session)
    : ProtocolHandlerBase(
        session,
        Substitute.For<INodeStatsManager>(),
        Substitute.For<IMessageSerializationService>(),
        Substitute.For<IBackgroundTaskScheduler>(),
        LimboLogs.Instance)
{
    public static RecordingProtocolHandler Create<TMessage>(List<TMessage>? recordedSends = null)
        where TMessage : P2PMessage
    {
        ISession session = Substitute.For<ISession>();
        session.When(s => s.DeliverMessage(Arg.Any<TMessage>()))
            .Do(c => recordedSends?.Add(c.Arg<TMessage>()));
        return new RecordingProtocolHandler(session);
    }

    public override string Name => "test";
    protected override TimeSpan InitTimeout => TimeSpan.Zero;
    public override byte ProtocolVersion => 0;
    public override string ProtocolCode => "test";
    public override int MessageIdSpaceSize => 0;
    public override void Init() { }
    public override void HandleMessage(Packet message) { }
    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) { }
    public override void Dispose() { }
}
