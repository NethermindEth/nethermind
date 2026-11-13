// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.Discovery;

internal sealed class AddressBurstLimiter
{
    private readonly Lazy<NodeFilter>[] _filters;

    public AddressBurstLimiter(int burstPerAddress, int filterSize, TimeSpan window)
    {
        _filters = new Lazy<NodeFilter>[Math.Max(1, burstPerAddress)];
        Func<NodeFilter> createFilter = () => NodeFilter.CreateExact(Math.Max(1, filterSize), window);
        for (int i = 0; i < _filters.Length; i++)
        {
            _filters[i] = new Lazy<NodeFilter>(createFilter);
        }
    }

    public AddressBurstLimiter(NodeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _filters = [new Lazy<NodeFilter>(() => filter)];
    }

    public bool TryAccept(IPAddress address)
    {
        Lazy<NodeFilter>[] filters = _filters;
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
