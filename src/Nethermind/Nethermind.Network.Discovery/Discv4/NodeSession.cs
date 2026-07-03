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
    private EndpointEntry[]? _receivedPingsByEndpoint;
    private int _receivedPingsByEndpointCount;
    private EndpointEntry[]? _receivedPongsByEndpoint;
    private int _receivedPongsByEndpointCount;
    private EndpointEntry[]? _pendingBondingPingsByEndpoint;
    private int _pendingBondingPingsByEndpointCount;
    private readonly object _endpointBondLock = new();
    private TaskCompletionSource? _endpointBondChanged;

    public bool HasReceivedPing => HasFreshReceivedPing();
    public bool NotTooManyFailure => Volatile.Read(ref _authenticatedRequestFailureCount) <= AuthenticatedRequestFailureLimit;
    public bool HasReceivedPong => HasFreshReceivedPong();
    public bool HasTriedPingRecently => Volatile.Read(ref _lastPingSentTicks) + PingRetryTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public bool HasReceivedPingFrom(IPEndPoint endpoint) => HasFreshReceivedPingFrom(endpoint);

    public bool HasPendingBondingPing(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            return ContainsEndpoint(_pendingBondingPingsByEndpoint, _pendingBondingPingsByEndpointCount, endpointKey);
        }
    }

    public bool HasEndpointBond(IPEndPoint endpoint) => HasFreshReceivedPongFrom(endpoint);

    public void ResetAuthenticatedRequestFailure() => Interlocked.Exchange(ref _authenticatedRequestFailureCount, 0);
    public void OnAuthenticatedRequestFailure() => Interlocked.Increment(ref _authenticatedRequestFailureCount);

    public void OnPongReceived(IPEndPoint endpoint)
    {
        lock (_endpointBondLock)
        {
            RecordEndpointTimestamp(
                ref _receivedPongsByEndpoint,
                ref _receivedPongsByEndpointCount,
                endpoint,
                Timestamper.UtcNow.Ticks);
        }

        SignalEndpointBondChanged();
    }

    public void OnPingReceived(IPEndPoint endpoint)
    {
        lock (_endpointBondLock)
        {
            RecordEndpointTimestamp(
                ref _receivedPingsByEndpoint,
                ref _receivedPingsByEndpointCount,
                endpoint,
                Timestamper.UtcNow.Ticks);
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
        bool evicted;
        lock (_endpointBondLock)
        {
            evicted = RecordEndpointEntry(
                ref _pendingBondingPingsByEndpoint,
                ref _pendingBondingPingsByEndpointCount,
                new EndpointKey(endpoint),
                token);
        }

        if (evicted)
        {
            SignalEndpointBondChanged();
        }

        return token;
    }

    public void OnPingCompleted(IPEndPoint endpoint, long token)
    {
        EndpointKey endpointKey = new(endpoint);
        bool removed;
        lock (_endpointBondLock)
        {
            removed = RemoveEndpointEntry(
                _pendingBondingPingsByEndpoint,
                ref _pendingBondingPingsByEndpointCount,
                endpointKey,
                token);
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

        EndpointKey endpointKey = new(endpoint);
        while (true)
        {
            Task bondChanged;
            lock (_endpointBondLock)
            {
                long nowTicks = Timestamper.UtcNow.Ticks;
                PruneExpiredEndpoints(_receivedPongsByEndpoint, ref _receivedPongsByEndpointCount, nowTicks);
                if (HasFreshEndpoint(_receivedPongsByEndpoint, _receivedPongsByEndpointCount, endpointKey, nowTicks))
                {
                    return true;
                }

                if (!ContainsEndpoint(_pendingBondingPingsByEndpoint, _pendingBondingPingsByEndpointCount, endpointKey))
                {
                    return false;
                }

                bondChanged = (_endpointBondChanged ??= NewEndpointBondChangedSource()).Task;
            }

            try
            {
                await bondChanged.WaitAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                return HasEndpointBond(endpoint);
            }
        }
    }

    private bool HasFreshReceivedPing()
    {
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredEndpoints(_receivedPingsByEndpoint, ref _receivedPingsByEndpointCount, nowTicks);
            return _receivedPingsByEndpointCount != 0;
        }
    }

    private bool HasFreshReceivedPong()
    {
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredEndpoints(_receivedPongsByEndpoint, ref _receivedPongsByEndpointCount, nowTicks);
            return _receivedPongsByEndpointCount != 0;
        }
    }

    private bool HasFreshReceivedPingFrom(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredEndpoints(_receivedPingsByEndpoint, ref _receivedPingsByEndpointCount, nowTicks);
            return HasFreshEndpoint(_receivedPingsByEndpoint, _receivedPingsByEndpointCount, endpointKey, nowTicks);
        }
    }

    private bool HasFreshReceivedPongFrom(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredEndpoints(_receivedPongsByEndpoint, ref _receivedPongsByEndpointCount, nowTicks);
            return HasFreshEndpoint(_receivedPongsByEndpoint, _receivedPongsByEndpointCount, endpointKey, nowTicks);
        }
    }

    private static void RecordEndpointTimestamp(
        ref EndpointEntry[]? endpoints,
        ref int count,
        IPEndPoint endpoint,
        long nowTicks)
    {
        PruneExpiredEndpoints(endpoints, ref count, nowTicks);
        RecordEndpointEntry(ref endpoints, ref count, new EndpointKey(endpoint), nowTicks);
    }

    private static bool RecordEndpointEntry(
        ref EndpointEntry[]? endpoints,
        ref int count,
        EndpointKey endpoint,
        long stamp)
    {
        endpoints ??= new EndpointEntry[MaxRememberedEndpointsPerSession];
        for (int i = 0; i < count; i++)
        {
            if (endpoints[i].Endpoint.Equals(endpoint))
            {
                endpoints[i] = new(endpoint, stamp);
                return false;
            }
        }

        if (count < endpoints.Length)
        {
            endpoints[count++] = new(endpoint, stamp);
            return false;
        }

        endpoints[GetOldestEndpointIndex(endpoints)] = new(endpoint, stamp);
        return true;
    }

    private static bool RemoveEndpointEntry(
        EndpointEntry[]? endpoints,
        ref int count,
        EndpointKey endpoint,
        long expectedStamp)
    {
        if (endpoints is null) return false;

        for (int i = 0; i < count; i++)
        {
            if (!endpoints[i].Endpoint.Equals(endpoint) || endpoints[i].Stamp != expectedStamp)
            {
                continue;
            }

            count--;
            endpoints[i] = endpoints[count];
            endpoints[count] = default;
            return true;
        }

        return false;
    }

    private static bool ContainsEndpoint(EndpointEntry[]? endpoints, int count, EndpointKey endpoint)
    {
        if (endpoints is null) return false;

        for (int i = 0; i < count; i++)
        {
            if (endpoints[i].Endpoint.Equals(endpoint))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFreshEndpoint(EndpointEntry[]? endpoints, int count, EndpointKey endpoint, long nowTicks)
    {
        if (endpoints is null) return false;

        for (int i = 0; i < count; i++)
        {
            EndpointEntry entry = endpoints[i];
            if (entry.Endpoint.Equals(endpoint) && IsValid(entry.Stamp, nowTicks))
            {
                return true;
            }
        }

        return false;
    }

    private static void PruneExpiredEndpoints(EndpointEntry[]? endpoints, ref int count, long nowTicks)
    {
        if (endpoints is null) return;

        int i = 0;
        while (i < count)
        {
            if (IsValid(endpoints[i].Stamp, nowTicks))
            {
                i++;
                continue;
            }

            count--;
            endpoints[i] = endpoints[count];
            endpoints[count] = default;
        }
    }

    private static int GetOldestEndpointIndex(EndpointEntry[] endpoints)
    {
        int oldestIndex = 0;
        long oldestStamp = endpoints[0].Stamp;
        for (int i = 1; i < endpoints.Length; i++)
        {
            if (endpoints[i].Stamp >= oldestStamp) continue;

            oldestIndex = i;
            oldestStamp = endpoints[i].Stamp;
        }

        return oldestIndex;
    }

    private static bool IsValid(long ticks, long nowTicks) =>
        ticks + BondTimeout.Ticks > nowTicks;

    private static TaskCompletionSource NewEndpointBondChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalEndpointBondChanged()
    {
        TaskCompletionSource? completion;
        lock (_endpointBondLock)
        {
            completion = _endpointBondChanged;
            _endpointBondChanged = null;
        }

        completion?.TrySetResult();
    }

    private readonly record struct EndpointEntry(EndpointKey Endpoint, long Stamp);

    private readonly record struct EndpointKey(IPAddress Address, int Port)
    {
        public EndpointKey(IPEndPoint endpoint)
            : this(endpoint.Address, endpoint.Port)
        {
        }
    }
}
