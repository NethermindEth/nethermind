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
    private const int MaxEndpointReceiptsPerSession = 16;

    private int _authenticatedRequestFailureCount;
    private long _lastPingSentTicks;
    private long _lastPingToken;
    private readonly Dictionary<EndpointKey, long> _pingReceipts = [];
    private readonly Dictionary<EndpointKey, long> _pongProofs = [];
    private readonly Dictionary<EndpointKey, long> _pendingEndpointProofs = [];
    private readonly object _endpointStateLock = new();
    private TaskCompletionSource _endpointProofChanged = NewEndpointProofChangedSource();

    public bool HasReceivedPing => HasValidReceipt(_pingReceipts);
    public bool NotTooManyFailure => Volatile.Read(ref _authenticatedRequestFailureCount) <= AuthenticatedRequestFailureLimit;
    public bool HasReceivedPong => HasValidReceipt(_pongProofs);
    public bool HasTriedPingRecently => Volatile.Read(ref _lastPingSentTicks) + PingRetryTimeout.Ticks > Timestamper.UtcNow.Ticks;
    public bool HasReceivedPingFrom(IPEndPoint endpoint) => HasValidReceipt(_pingReceipts, endpoint);

    public bool HasPendingEndpointProof(IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointStateLock)
        {
            return _pendingEndpointProofs.ContainsKey(endpointKey);
        }
    }

    public bool HasEndpointProof(IPEndPoint endpoint) => HasValidReceipt(_pongProofs, endpoint);

    public void ResetAuthenticatedRequestFailure() => Interlocked.Exchange(ref _authenticatedRequestFailureCount, 0);
    public void OnAuthenticatedRequestFailure() => Interlocked.Increment(ref _authenticatedRequestFailureCount);

    public void OnPongReceived(IPEndPoint endpoint)
    {
        lock (_endpointStateLock)
        {
            RecordEndpointReceipt(_pongProofs, endpoint, Timestamper.UtcNow.Ticks);
        }

        SignalEndpointProofChanged();
    }

    public void OnPingReceived(IPEndPoint endpoint)
    {
        lock (_endpointStateLock)
        {
            RecordEndpointReceipt(_pingReceipts, endpoint, Timestamper.UtcNow.Ticks);
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
        lock (_endpointStateLock)
        {
            _pendingEndpointProofs[new EndpointKey(endpoint)] = token;
        }

        SignalEndpointProofChanged();
        return token;
    }

    public void OnPingCompleted(IPEndPoint endpoint, long token)
    {
        EndpointKey endpointKey = new(endpoint);
        bool removed;
        lock (_endpointStateLock)
        {
            removed = _pendingEndpointProofs.TryGetValue(endpointKey, out long pendingToken)
                && pendingToken == token
                && _pendingEndpointProofs.Remove(endpointKey);
        }

        if (removed)
        {
            SignalEndpointProofChanged();
        }
    }

    public async ValueTask<bool> WaitForEndpointProof(IPEndPoint endpoint, TimeSpan timeout, CancellationToken token)
    {
        using CancellationTokenSource timeoutCts = new(timeout);
        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

        while (!HasEndpointProof(endpoint))
        {
            if (!HasPendingEndpointProof(endpoint)) return false;

            Task proofChanged = Volatile.Read(ref _endpointProofChanged).Task;
            if (HasEndpointProof(endpoint)) return true;
            if (!HasPendingEndpointProof(endpoint)) return false;

            try
            {
                await proofChanged.WaitAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                return HasEndpointProof(endpoint);
            }
        }

        return true;
    }

    private bool HasValidReceipt(Dictionary<EndpointKey, long> receipts)
    {
        lock (_endpointStateLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredReceipts(receipts, nowTicks);
            foreach (long ticks in receipts.Values)
            {
                if (IsValid(ticks, nowTicks)) return true;
            }

            return false;
        }
    }

    private bool HasValidReceipt(Dictionary<EndpointKey, long> receipts, IPEndPoint endpoint)
    {
        EndpointKey endpointKey = new(endpoint);
        lock (_endpointStateLock)
        {
            long nowTicks = Timestamper.UtcNow.Ticks;
            PruneExpiredReceipts(receipts, nowTicks);
            return receipts.TryGetValue(endpointKey, out long ticks) && IsValid(ticks, nowTicks);
        }
    }

    private void RecordEndpointReceipt(Dictionary<EndpointKey, long> receipts, IPEndPoint endpoint, long nowTicks)
    {
        PruneExpiredReceipts(receipts, nowTicks);
        receipts[new EndpointKey(endpoint)] = nowTicks;
        TrimEndpointReceipts(receipts);
    }

    private static void PruneExpiredReceipts(Dictionary<EndpointKey, long> receipts, long nowTicks)
    {
        List<EndpointKey>? expiredEndpoints = null;
        foreach ((EndpointKey endpoint, long ticks) in receipts)
        {
            if (!IsValid(ticks, nowTicks))
            {
                (expiredEndpoints ??= []).Add(endpoint);
            }
        }

        if (expiredEndpoints is null) return;
        foreach (EndpointKey endpoint in expiredEndpoints)
        {
            receipts.Remove(endpoint);
        }
    }

    private static void TrimEndpointReceipts(Dictionary<EndpointKey, long> receipts)
    {
        while (receipts.Count > MaxEndpointReceiptsPerSession)
        {
            EndpointKey oldestEndpoint = default;
            long oldestTicks = long.MaxValue;
            foreach ((EndpointKey endpoint, long ticks) in receipts)
            {
                if (ticks >= oldestTicks) continue;
                oldestEndpoint = endpoint;
                oldestTicks = ticks;
            }

            receipts.Remove(oldestEndpoint);
        }
    }

    private static bool IsValid(long ticks, long nowTicks) =>
        ticks + BondTimeout.Ticks > nowTicks;

    private static TaskCompletionSource NewEndpointProofChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalEndpointProofChanged() =>
        Interlocked.Exchange(ref _endpointProofChanged, NewEndpointProofChangedSource()).TrySetResult();

    private readonly record struct EndpointKey(IPAddress Address, int Port)
    {
        public EndpointKey(IPEndPoint endpoint)
            : this(endpoint.Address, endpoint.Port)
        {
        }
    }
}
