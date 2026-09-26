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

    private int _authenticatedRequestFailureCount;
    private long _lastPingSentTicks;
    private long _lastPingToken;
    private readonly Lock _endpointBondLock = new();
    private EndpointBondTable _receivedPings;
    private EndpointBondTable _receivedPongs;
    private EndpointBondTable _pendingBondingPings;
    private TaskCompletionSource? _endpointBondChanged;

    public bool NotTooManyFailure => Volatile.Read(ref _authenticatedRequestFailureCount) <= AuthenticatedRequestFailureLimit;
    public bool HasTriedPingRecently => Volatile.Read(ref _lastPingSentTicks) + PingRetryTimeout.Ticks > Timestamper.UtcNow.Ticks;

    public bool HasReceivedPing
    {
        get
        {
            lock (_endpointBondLock)
            {
                _receivedPings.PruneStale(StaleBondStamp);
                return !_receivedPings.IsEmpty;
            }
        }
    }

    public bool HasReceivedPong
    {
        get
        {
            lock (_endpointBondLock)
            {
                _receivedPongs.PruneStale(StaleBondStamp);
                return !_receivedPongs.IsEmpty;
            }
        }
    }

    public bool HasReceivedPingFrom(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            long minValidStamp = StaleBondStamp;
            _receivedPings.PruneStale(minValidStamp);
            return _receivedPings.HasFresh(endpointKey, minValidStamp);
        }
    }

    public bool HasEndpointBond(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            long minValidStamp = StaleBondStamp;
            _receivedPongs.PruneStale(minValidStamp);
            return _receivedPongs.HasFresh(endpointKey, minValidStamp);
        }
    }

    public bool HasPendingBondingPing(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            return _pendingBondingPings.Contains(endpointKey);
        }
    }

    public void ResetAuthenticatedRequestFailure() => Interlocked.Exchange(ref _authenticatedRequestFailureCount, 0);
    public void OnAuthenticatedRequestFailure() => Interlocked.Increment(ref _authenticatedRequestFailureCount);

    public void OnPongReceived(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            _receivedPongs.PruneStale(nowTicks - BondTimeout.Ticks);
            _receivedPongs.Record(endpointKey, nowTicks);
        }

        SignalEndpointBondChanged();
    }

    public void OnPingReceived(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointBondLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            _receivedPings.PruneStale(nowTicks - BondTimeout.Ticks);
            _receivedPings.Record(endpointKey, nowTicks);
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
        EndpointKey endpointKey = new(endpoint);
        bool evicted;
        lock (_endpointBondLock)
        {
            evicted = _pendingBondingPings.Record(endpointKey, token);
        }

        // An eviction drops another endpoint's pending ping, so wake its waiter to return false instead of timing out.
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
            removed = _pendingBondingPings.Remove(endpointKey, token);
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
                long minValidStamp = StaleBondStamp;
                _receivedPongs.PruneStale(minValidStamp);
                if (_receivedPongs.HasFresh(endpointKey, minValidStamp))
                {
                    return true;
                }

                if (!_pendingBondingPings.Contains(endpointKey))
                {
                    return false;
                }

                bondChanged = (_endpointBondChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
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

    // Endpoint stamps recorded at or before this tick predate the bond window and are treated as expired.
    private long StaleBondStamp => Timestamper.UtcNow.Ticks - BondTimeout.Ticks;
}
