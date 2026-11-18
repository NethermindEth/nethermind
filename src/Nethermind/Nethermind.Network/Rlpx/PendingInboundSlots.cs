// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Nethermind.Network.Rlpx;

internal sealed class PendingInboundSlots(int ordinaryLimit, int sourceLimit)
{
    internal const int PrivilegedLimit = 8;
    private readonly Lock _lock = new();
    private readonly Dictionary<NodeFilter.IpSubnetKey, int> _sources = [];
    private readonly Dictionary<NodeFilter.IpSubnetKey, int> _prefixes = [];
    private int _ordinary;
    private int _privileged;

    internal readonly record struct Source(NodeFilter.IpSubnetKey Key, NodeFilter.IpSubnetKey? Prefix)
    {
        internal static Source From(IPAddress address)
        {
            ParsedIPAddress ip = ParsedIPAddress.Parse(address);
            return new(NodeFilter.IpSubnetKey.CreateFromParsed(in ip, 32, 64),
                ip.Family == IpFamily.IPv6 ? NodeFilter.IpSubnetKey.CreateFromParsed(in ip, 32, 56) : null);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _ordinary + _privileged;
        }
    }

    public bool TryReserve(Source source, bool privileged, out bool sourceLimitReached)
    {
        lock (_lock)
        {
            sourceLimitReached = false;
            if (ordinaryLimit == 0) return false;
            if (privileged)
            {
                if (_privileged == PrivilegedLimit) return false;
                _privileged++;
                return true;
            }

            int count = _sources.GetValueOrDefault(source.Key);
            int prefixCount = source.Prefix is { } prefix ? _prefixes.GetValueOrDefault(prefix) : 0;
            sourceLimitReached = count >= sourceLimit || prefixCount >= 4L * sourceLimit;
            if (sourceLimitReached || _ordinary == ordinaryLimit) return false;
            _sources[source.Key] = count + 1;
            if (source.Prefix is { } prefixKey) _prefixes[prefixKey] = prefixCount + 1;
            _ordinary++;
            return true;
        }
    }

    public void Release(Source source, bool privileged)
    {
        lock (_lock)
        {
            if (privileged)
            {
                _privileged--;
                return;
            }
            _ordinary--;
            Decrement(_sources, source.Key);
            if (source.Prefix is { } prefix) Decrement(_prefixes, prefix);
        }
    }

    private static void Decrement(Dictionary<NodeFilter.IpSubnetKey, int> counts, NodeFilter.IpSubnetKey key)
    {
        int count = counts[key];
        if (count == 1) counts.Remove(key);
        else counts[key] = count - 1;
    }
}
