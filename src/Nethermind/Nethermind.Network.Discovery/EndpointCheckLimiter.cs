// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

internal sealed class EndpointCheckLimiter(int maximumActive)
{
    private const int MaximumPerSource = 4;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private readonly LruCache<(ValueHash256, IPEndPoint), long> _recent = new(4096, "discovery endpoint checks");
    private readonly LruCache<ValueHash256, long> _refreshes = new(4096, "discovery record refreshes");
    private readonly HashSet<ValueHash256> _refreshing = [];
    private readonly Dictionary<(ValueHash256, IPEndPoint), IDisposable> _endpointReservations = [];
    private readonly Dictionary<AddressBurstLimiter.AddressKey, int> _active = [];
    private readonly Lock _lock = new();
    private int _count;
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal void Mark(Node node)
    {
        lock (_lock)
        {
            (ValueHash256, IPEndPoint) endpoint = (node.Id.Hash.ValueHash256, node.DiscoveryAddress);
            _recent.Set(endpoint, TimeProvider.GetTimestamp());
        }
    }

    internal bool TryReserve(Node node)
    {
        lock (_lock)
        {
            if (_endpointReservations.ContainsKey((node.Id.Hash.ValueHash256, node.DiscoveryAddress)) || !TryReserve(node, false, out IDisposable? lease)) return false;
            _endpointReservations.Add((node.Id.Hash.ValueHash256, node.DiscoveryAddress), lease!);
            return true;
        }
    }

    internal bool TryReserveEndpoint(Node node, out IDisposable? lease) => TryReserve(node, false, out lease);

    internal bool TryReserveRefresh(Node node, out IDisposable? lease) => TryReserve(node, true, out lease);

    private bool TryReserve(Node node, bool refresh, out IDisposable? lease)
    {
        lease = null;
        ValueHash256 identity = node.Id.Hash.ValueHash256;
        (ValueHash256, IPEndPoint) endpoint = (identity, node.DiscoveryAddress);
        AddressBurstLimiter.AddressKey source = AddressBurstLimiter.GetKey(node.DiscoveryAddress.Address);
        lock (_lock)
        {
            long now = TimeProvider.GetTimestamp();
            if (refresh)
            {
                if (_refreshing.Contains(identity) || _refreshes.TryGet(identity, out long started) && TimeProvider.GetElapsedTime(started, now) < Cooldown) return false;
            }
            else if (_recent.TryGet(endpoint, out long started) && TimeProvider.GetElapsedTime(started, now) < Cooldown) return false;
            _active.TryGetValue(source, out int active);
            if (active >= MaximumPerSource || _count >= maximumActive) return false;
            if (refresh)
            {
                _refreshes.Set(identity, now);
                _refreshing.Add(identity);
            }
            else _recent.Set(endpoint, now);
            _active[source] = active + 1;
            _count++;
            lease = new Reservation(this, source, identity, refresh);
            return true;
        }
    }

    internal void Release(Node node)
    {
        IDisposable? lease;
        lock (_lock) _endpointReservations.Remove((node.Id.Hash.ValueHash256, node.DiscoveryAddress), out lease);
        lease?.Dispose();
    }

    private sealed class Reservation(EndpointCheckLimiter owner, AddressBurstLimiter.AddressKey source, ValueHash256 identity, bool refresh) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._lock)
            {
                int remaining = owner._active[source] - 1;
                if (remaining == 0) owner._active.Remove(source);
                else owner._active[source] = remaining;
                if (refresh) owner._refreshing.Remove(identity);
                owner._count--;
            }
        }
    }
}
