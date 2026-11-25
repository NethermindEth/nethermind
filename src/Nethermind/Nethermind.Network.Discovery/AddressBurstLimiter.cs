// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.Discovery;

internal sealed class AddressBurstLimiter
{
    private readonly NodeFilter _first;
    private readonly Lazy<NodeFilter>[] _burstFilters;

    public AddressBurstLimiter(int burstPerAddress, int filterSize, TimeSpan window)
    {
        _first = NodeFilter.CreateExact(Math.Max(1, filterSize), window);
        int remaining = Math.Max(1, burstPerAddress) - 1;
        _burstFilters = remaining == 0 ? [] : new Lazy<NodeFilter>[remaining];
        Func<NodeFilter> createFilter = () => NodeFilter.CreateExact(Math.Max(1, filterSize), window);
        for (int i = 0; i < _burstFilters.Length; i++)
        {
            _burstFilters[i] = new Lazy<NodeFilter>(createFilter);
        }
    }

    public AddressBurstLimiter(NodeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _first = filter;
        _burstFilters = [];
    }

    public bool TryAccept(IPAddress address)
    {
        if (_first.TryAccept(address, exactOnly: true)) return true;
        Lazy<NodeFilter>[] filters = _burstFilters;
        for (int i = 0; i < filters.Length; i++)
        {
            if (filters[i].Value.TryAccept(address, exactOnly: true))
            {
                return true;
            }
        }

        return false;
    }
}
