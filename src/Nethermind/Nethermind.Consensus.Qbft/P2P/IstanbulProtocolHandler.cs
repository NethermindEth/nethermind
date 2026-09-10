// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Consensus.Qbft.P2P;

/// <summary>
/// The <c>istanbul/100</c> satellite protocol Besu speaks between QBFT validators. It has no handshake:
/// every frame is a consensus message that is queued for the state machine as-is.
/// </summary>
/// <remarks>
/// The message id space is <see cref="QbftMessageCode.MessageSpace"/> wide even though only four codes
/// are used, because the adaptive ids negotiated in the Hello exchange depend on it.
/// </remarks>
public sealed class IstanbulProtocolHandler(
    ISession session,
    INodeStatsManager nodeStats,
    IMessageSerializationService serializer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    IBftEventQueue eventQueue,
    ValidatorPeers validatorPeers,
    ILogManager logManager)
    : ZeroProtocolHandlerBase(session, nodeStats, serializer, backgroundTaskScheduler, logManager), IStaticProtocolInfo, IQbftPeer
{
    public const byte IstanbulVersion = 100;

    public static byte Version => IstanbulVersion;
    public static string Code => QbftWireMessage.ProtocolName;

    public override string Name => "istanbul100";
    public override byte ProtocolVersion => Version;
    public override string ProtocolCode => Code;
    public override int MessageIdSpaceSize => QbftMessageCode.MessageSpace;
    protected override TimeSpan InitTimeout => Timeouts.Eth;

    public Address NodeAddress => Session.RemoteNodeId.Address;

    public override void Init()
    {
        validatorPeers.Add(this);
        NotifyProtocolInitialized(new ProtocolInitializedEventArgs(this));
    }

    public override void Dispose()
    {
        validatorPeers.Remove(this);
        ClearProtocolEvents();
    }

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int code = message.PacketType;
        if (!QbftMessageCode.IsValid(code))
        {
            return false;
        }

        int size = message.Content.ReadableBytes;
        byte[] data = new byte[size];
        message.Content.ReadBytes(data);
        ReportIn(QbftMessageCode.Name(code), size);
        eventQueue.Add(new ReceivedMessageEvent(new QbftReceivedMessage(code, data, NodeAddress)));
        return true;
    }

    public void Send(int code, byte[] data) => Send(QbftWireMessage.Create(code, data));
}

/// <summary>Advertises <c>istanbul/100</c> in the Hello handshake.</summary>
public sealed class QbftP2PCapabilityResolver : IP2PCapabilityResolver
{
    public static readonly Capability IstanbulCapability = new(QbftWireMessage.ProtocolName, IstanbulProtocolHandler.IstanbulVersion);

    // The capability set is static, so the cache never needs invalidating.
    public event Action? Changed { add { } remove { } }

    public void Resolve(ISet<Capability> capabilities) => capabilities.Add(IstanbulCapability);
}
