// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

internal record NodeSession(INodeStats NodeStats)
{
    private static readonly TimeSpan BondTimeout = TimeSpan.FromHours(12);
    private const int AuthenticatedRequestFailureLimit = 5;
    private long AuthenticatedRequestFailureCount { get; set; }
    private DateTimeOffset LastPongReceived { get; set; } = DateTimeOffset.MinValue;
    private DateTimeOffset LastPingReceived { get; set; } = DateTimeOffset.MinValue;

    public bool HasReceivedPing => LastPingReceived + BondTimeout > DateTimeOffset.UtcNow;
    public bool NotTooManyFailure => AuthenticatedRequestFailureCount <= AuthenticatedRequestFailureLimit;
    public bool HasReceivedPong => LastPongReceived + BondTimeout > DateTimeOffset.UtcNow;

    public void ResetAuthenticatedRequestFailure() => AuthenticatedRequestFailureCount = 0;
    public void OnAuthenticatedRequestFailure() => AuthenticatedRequestFailureCount++;

    public void OnPongReceived() => LastPongReceived = DateTimeOffset.UtcNow;
    public void OnPingReceived() => LastPingReceived = DateTimeOffset.UtcNow;

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
}
