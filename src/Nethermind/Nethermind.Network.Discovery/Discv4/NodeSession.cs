// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public sealed record NodeSession(INodeStats NodeStats, ITimestamper Timestamper)
{
    public static readonly TimeSpan BondTimeout = TimeSpan.FromHours(12);
    public static readonly TimeSpan PingRetryTimeout = TimeSpan.FromMinutes(10);
    public const int AuthenticatedRequestFailureLimit = 5;

    private int _authenticatedRequestFailureCount;
    private long _lastPongReceivedTicks;
    private long _lastPingReceivedTicks;
    private long _lastPingSentTicks;
    private Node? _enrNode;
    private IPEndPoint? _lastPongEndpoint;

    public bool HasReceivedPing => Volatile.Read(ref _lastPingReceivedTicks) + BondTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public bool NotTooManyFailure => Volatile.Read(ref _authenticatedRequestFailureCount) <= AuthenticatedRequestFailureLimit;
    public bool HasReceivedPong => Volatile.Read(ref _lastPongReceivedTicks) + BondTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public bool HasTriedPingRecently => Volatile.Read(ref _lastPingSentTicks) + PingRetryTimeout.Ticks > Timestamper.UtcNow.Ticks;
    internal Node? EnrNode
    {
        get => Volatile.Read(ref _enrNode);
        set => Volatile.Write(ref _enrNode, value);
    }

    public bool HasEndpointProof(IPEndPoint endpoint) =>
        HasReceivedPong && Volatile.Read(ref _lastPongEndpoint) is { } lastPongEndpoint && lastPongEndpoint.Equals(endpoint);

    public void ResetAuthenticatedRequestFailure() => Interlocked.Exchange(ref _authenticatedRequestFailureCount, 0);
    public void OnAuthenticatedRequestFailure() => Interlocked.Increment(ref _authenticatedRequestFailureCount);

    public void OnPongReceived(IPEndPoint endpoint)
    {
        Volatile.Write(ref _lastPongEndpoint, endpoint);
        Volatile.Write(ref _lastPongReceivedTicks, Timestamper.UtcNow.Ticks);
    }

    public void OnPingReceived() => Volatile.Write(ref _lastPingReceivedTicks, Timestamper.UtcNow.Ticks);

    public void RecordStatsForOutgoingMsg(DiscoveryMsg msg) => RecordStatsForMsg(msg, outgoing: true);
    public void RecordStatsForIncomingMsg(DiscoveryMsg msg) => RecordStatsForMsg(msg, outgoing: false);

    private void RecordStatsForMsg(DiscoveryMsg msg, bool outgoing)
    {
        // The Discovery* members in NodeStatsEventType are laid out as ...Out, ...In pairs,
        // so the incoming counterpart is always one position after the outgoing one.
        NodeStatsEventType eventType = msg.MsgType switch
        {
            MsgType.Ping => NodeStatsEventType.DiscoveryPingOut,
            MsgType.FindNode => NodeStatsEventType.DiscoveryFindNodeOut,
            MsgType.EnrRequest => NodeStatsEventType.DiscoveryEnrRequestOut,
            MsgType.Neighbors => NodeStatsEventType.DiscoveryNeighboursOut,
            MsgType.Pong => NodeStatsEventType.DiscoveryPongOut,
            MsgType.EnrResponse => NodeStatsEventType.DiscoveryEnrResponseOut,
            _ => NodeStatsEventType.None,
        };
        if (eventType == NodeStatsEventType.None) return;
        NodeStats.AddNodeStatsEvent(outgoing ? eventType : eventType + 1);
    }

    public void OnPingSent() => Volatile.Write(ref _lastPingSentTicks, Timestamper.UtcNow.Ticks);
}
