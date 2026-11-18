// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Nethermind.Network;

/// <summary>Bounds authenticated-source history while preserving live-source restrictions.</summary>
internal sealed class InboundNodeFilter(int capacity, bool exactOnly, IPAddress? currentIp, TimeProvider? timeProvider = null)
{
    internal const int DefaultCapacity = 4096;

    internal bool WasRecentlyAccepted(IPAddress address)
    {
        NodeFilter.IpSubnetKey key = NodeFilter.GetKey(address, exactOnly, _currentIp);
        lock (_lock)
            return _entries.TryGetValue(key, out LinkedListNode<Entry>? entry)
                && _timeProvider.GetElapsedTime(entry.Value.Timestamp) < Timeout;
    }
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ParsedIPAddress? _currentIp = currentIp is null ? null : ParsedIPAddress.Parse(currentIp);
    private readonly Dictionary<NodeFilter.IpSubnetKey, LinkedListNode<Entry>> _entries = [with(capacity)];
    private readonly LinkedList<Entry> _expiryOrder = new();
    private readonly Lock _lock = new();

    public bool TryAccept(IPAddress address, IReadOnlySet<NodeFilter.IpSubnetKey>? liveSources = null)
    {
        NodeFilter.IpSubnetKey key = NodeFilter.GetKey(address, exactOnly, _currentIp);
        using Lock.Scope scope = _lock.EnterScope();
        long now = _timeProvider.GetTimestamp();
        LinkedListNode<Entry>? entry = _expiryOrder.First;
        while (entry is not null && _timeProvider.GetElapsedTime(entry.Value.Timestamp, now) >= Timeout)
        {
            LinkedListNode<Entry>? next = entry.Next;
            if (liveSources?.Contains(entry.Value.Key) != true) Remove(entry);
            entry = next;
        }

        if (_entries.ContainsKey(key)) return false;
        if (_entries.Count >= capacity)
        {
            entry = _expiryOrder.First;
            while (entry is not null && liveSources?.Contains(entry.Value.Key) == true) entry = entry.Next;
            if (entry is null) return true;
            Remove(entry);
        }
        _entries.Add(key, _expiryOrder.AddLast(new Entry(key, now)));
        return true;
    }

    private void Remove(LinkedListNode<Entry> entry)
    {
        _entries.Remove(entry.Value.Key);
        _expiryOrder.Remove(entry);
    }

    public void Touch(IPAddress address)
    {
        NodeFilter.IpSubnetKey key = NodeFilter.GetKey(address, exactOnly, _currentIp);
        using Lock.Scope scope = _lock.EnterScope();
        if (_entries.TryGetValue(key, out LinkedListNode<Entry>? entry))
        {
            entry.Value = new Entry(key, _timeProvider.GetTimestamp());
            _expiryOrder.Remove(entry);
            _expiryOrder.AddLast(entry);
        }
    }

    private readonly record struct Entry(NodeFilter.IpSubnetKey Key, long Timestamp);
}
