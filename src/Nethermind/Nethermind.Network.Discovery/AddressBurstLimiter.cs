// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.Discovery;

internal sealed class AddressBurstLimiter
{
    private readonly NodeFilter[] _filters;

    public AddressBurstLimiter(int burstPerAddress, int filterSize, TimeSpan window)
    {
        _filters = new NodeFilter[Math.Max(1, burstPerAddress)];
        for (int i = 0; i < _filters.Length; i++)
        {
            _filters[i] = NodeFilter.CreateExact(Math.Max(1, filterSize), window);
        }
    }

    public AddressBurstLimiter(NodeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _filters = [filter];
    }

    public bool TryAccept(IPAddress address)
    {
        NodeFilter[] filters = _filters;
        for (int i = 0; i < filters.Length; i++)
        {
            if (filters[i].TryAccept(address, exactOnly: true))
            {
                return true;
            }
        }

        return false;
    }
}
