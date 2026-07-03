// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
    private const int MaxRememberedEndpointsPerSession = 16;

    private int _authenticatedRequestFailureCount;
    private long _lastPingSentTicks;
    private long _lastPingToken;
    private readonly Dictionary<EndpointKey, long> _receivedPingsByEndpoint = [];
    private readonly Dictionary<EndpointKey, long> _receivedPongsByEndpoint = [];
    private readonly Dictionary<EndpointKey, long> _pendingBondingPingsByEndpoint = [];
    private readonly object _endpointBondLock = new();
    private TaskCompletionSource _endpointBondChanged = NewEndpointBondChangedSource();

    public bool HasReceivedPing => HasFreshEndpoint(_receivedPingsByEndpoint);
    public bool NotTooManyFailure => Volatile.Read(ref _authenticatedRequestFailureCount) <= AuthenticatedRequestFailureLimit;
    public bool HasReceivedPong => HasFreshEndpoint(_receivedPongsByEndpoint);
    public bool HasTriedPingRecently => Volatile.Read(ref _lastPingSentTicks) + PingRetryTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public bool HasReceivedPingFrom(IPEndPoint endpoint) => HasFreshEndpoint(_receivedPingsByEndpoint, endpoint);

    public bool HasPendingBondingPing(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            return _pendingBondingPingsByEndpoint.ContainsKey(endpointKey);
        }
    }

    public bool HasEndpointBond(IPEndPoint endpoint) => HasFreshEndpoint(_receivedPongsByEndpoint, endpoint);

    public void ResetAuthenticatedRequestFailure() => Interlocked.Exchange(ref _authenticatedRequestFailureCount, 0);
    public void OnAuthenticatedRequestFailure() => Interlocked.Increment(ref _authenticatedRequestFailureCount);

    public void OnPongReceived(IPEndPoint endpoint)
    {
        lock (_endpointBondLock)
        {
            RecordEndpointTimestamp(_receivedPongsByEndpoint, endpoint, Timestamper.UtcNow.Ticks);
        }

        SignalEndpointBondChanged();
    }

    public void OnPingReceived(IPEndPoint endpoint)
    {
        lock (_endpointBondLock)
        {
            RecordEndpointTimestamp(_receivedPingsByEndpoint, endpoint, Timestamper.UtcNow.Ticks);
        }
    }

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

    public long OnPingSent(IPEndPoint endpoint)
    {
        OnPingSent();
        long token = Interlocked.Increment(ref _lastPingToken);
        lock (_endpointBondLock)
        {
            _pendingBondingPingsByEndpoint[new EndpointKey(endpoint)] = token;
        }

        SignalEndpointBondChanged();
        return token;
    }

    public void OnPingCompleted(IPEndPoint endpoint, long token)
    {
        EndpointKey endpointKey = new(endpoint);
        bool removed;
        lock (_endpointBondLock)
        {
            removed = _pendingBondingPingsByEndpoint.TryGetValue(endpointKey, out long pendingToken)
                && pendingToken == token
                && _pendingBondingPingsByEndpoint.Remove(endpointKey);
        }

        if (removed)
        {
            SignalEndpointBondChanged();
        }
    }

    public async ValueTask<bool> WaitForEndpointBond(IPEndPoint endpoint, TimeSpan timeout, CancellationToken token)
    {
        using CancellationTokenSource timeoutCts = new(timeout);
        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

        while (!HasEndpointBond(endpoint))
        {
            if (!HasPendingBondingPing(endpoint)) return false;

            Task bondChanged = Volatile.Read(ref _endpointBondChanged).Task;
            if (HasEndpointBond(endpoint)) return true;
            if (!HasPendingBondingPing(endpoint)) return false;

            try
            {
                await bondChanged.WaitAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                return HasEndpointBond(endpoint);
            }
        }

        return true;
    }

    private bool HasFreshEndpoint(Dictionary<EndpointKey, long> endpoints)
    {
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredEndpoints(endpoints, nowTicks);
            foreach (long ticks in endpoints.Values)
            {
                if (IsValid(ticks, nowTicks)) return true;
            }

            return false;
        }
    }

    private bool HasFreshEndpoint(Dictionary<EndpointKey, long> endpoints, IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredEndpoints(endpoints, nowTicks);
            return endpoints.TryGetValue(endpointKey, out long ticks) && IsValid(ticks, nowTicks);
        }
    }

    private void RecordEndpointTimestamp(Dictionary<EndpointKey, long> endpoints, IPEndPoint endpoint, long nowTicks)
    {
        PruneExpiredEndpoints(endpoints, nowTicks);
        endpoints[new EndpointKey(endpoint)] = nowTicks;
        TrimRememberedEndpoints(endpoints);
    }

    private static void PruneExpiredEndpoints(Dictionary<EndpointKey, long> endpoints, long nowTicks)
    {
        List<EndpointKey>? expiredEndpoints = null;
        foreach ((EndpointKey endpoint, long ticks) in endpoints)
        {
            if (!IsValid(ticks, nowTicks))
            {
                (expiredEndpoints ??= []).Add(endpoint);
            }
        }

        if (expiredEndpoints is null) return;
        foreach (EndpointKey endpoint in expiredEndpoints)
        {
            endpoints.Remove(endpoint);
        }
    }

    private static void TrimRememberedEndpoints(Dictionary<EndpointKey, long> endpoints)
    {
        while (endpoints.Count > MaxRememberedEndpointsPerSession)
        {
            EndpointKey oldestEndpoint = default;
            long oldestTicks = long.MaxValue;
            foreach ((EndpointKey endpoint, long ticks) in endpoints)
            {
                if (ticks >= oldestTicks) continue;
                oldestEndpoint = endpoint;
                oldestTicks = ticks;
            }

            endpoints.Remove(oldestEndpoint);
        }
    }

    private static bool IsValid(long ticks, long nowTicks) =>
        ticks + BondTimeout.Ticks > nowTicks;

    private static TaskCompletionSource NewEndpointBondChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalEndpointBondChanged() =>
        Interlocked.Exchange(ref _endpointBondChanged, NewEndpointBondChangedSource()).TrySetResult();

    private readonly record struct EndpointKey(IPAddress Address, int Port)
    {
        public EndpointKey(IPEndPoint endpoint)
            : this(endpoint.Address, endpoint.Port)
        {
        }
    }
}
