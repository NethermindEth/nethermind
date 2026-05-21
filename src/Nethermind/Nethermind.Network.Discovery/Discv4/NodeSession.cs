// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public record NodeSession(INodeStats NodeStats, ITimestamper Timestamper)
{
    public static readonly TimeSpan BondTimeout = TimeSpan.FromHours(12);
    public static readonly TimeSpan PingRetryTimeout = TimeSpan.FromMinutes(10);
    public const int AuthenticatedRequestFailureLimit = 5;

    private int _authenticatedRequestFailureCount;
    private long _lastPongReceivedTicks;
    private long _lastPingReceivedTicks;
    private long _lastPingSentTicks;

    public bool HasReceivedPing => Volatile.Read(ref _lastPingReceivedTicks) + BondTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public bool NotTooManyFailure => Volatile.Read(ref _authenticatedRequestFailureCount) <= AuthenticatedRequestFailureLimit;
    public bool HasReceivedPong => Volatile.Read(ref _lastPongReceivedTicks) + BondTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public bool HasTriedPingRecently => Volatile.Read(ref _lastPingSentTicks) + PingRetryTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public void ResetAuthenticatedRequestFailure() => Interlocked.Exchange(ref _authenticatedRequestFailureCount, 0);
    public void OnAuthenticatedRequestFailure() => Interlocked.Increment(ref _authenticatedRequestFailureCount);

    public void OnPongReceived() => Volatile.Write(ref _lastPongReceivedTicks, Timestamper.UtcNow.Ticks);
    public void OnPingReceived() => Volatile.Write(ref _lastPingReceivedTicks, Timestamper.UtcNow.Ticks);

    public void RecordStatsForOutgoingMsg(DiscoveryMsg msg)
    {
        switch (msg.MsgType)
        {
            case MsgType.Ping:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingOut);
                break;
            case MsgType.FindNode:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeOut);
                break;
            case MsgType.EnrRequest:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrRequestOut);
                break;
            case MsgType.Neighbors:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryNeighboursOut);
                break;
            case MsgType.Pong:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongOut);
                break;
            case MsgType.EnrResponse:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrResponseOut);
                break;
        }
    }

    public void RecordStatsForIncomingMsg(DiscoveryMsg msg)
    {
        switch (msg.MsgType)
        {
            case MsgType.Ping:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingIn);
                break;
            case MsgType.FindNode:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeIn);
                break;
            case MsgType.EnrRequest:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrRequestIn);
                break;
            case MsgType.Neighbors:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryNeighboursIn);
                break;
            case MsgType.Pong:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongIn);
                break;
            case MsgType.EnrResponse:
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryEnrResponseIn);
                break;
        }
    }

    public void OnPingSent() => Volatile.Write(ref _lastPingSentTicks, Timestamper.UtcNow.Ticks);
}
