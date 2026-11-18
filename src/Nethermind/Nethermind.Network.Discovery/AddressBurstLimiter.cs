// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.Discovery;

internal sealed class AddressBurstLimiter
{
    private readonly NodeFilter? _filter;
    private readonly Dictionary<AddressKey, int> _counts = [];
    private readonly Lock _lock = new();
    private readonly int _burst;
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private long _windowStart;

    public AddressBurstLimiter(int burstPerAddress, int filterSize, TimeSpan window, TimeProvider? timeProvider = null)
    {
        _burst = Math.Max(1, burstPerAddress);
        _capacity = Math.Max(1, filterSize);
        _window = window;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowStart = _timeProvider.GetTimestamp();
    }

    public AddressBurstLimiter(NodeFilter filter) : this(1, 1, TimeSpan.Zero)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = filter;
    }

    public bool TryAccept(IPAddress address)
    {
        if (_filter is not null) return _filter.TryAccept(address, exactOnly: true);

        AddressKey key = GetKey(address);
        lock (_lock)
        {
            long now = _timeProvider.GetTimestamp();
            if (_timeProvider.GetElapsedTime(_windowStart, now) >= _window)
            {
                _counts.Clear();
                _windowStart = now;
            }

            if (_counts.TryGetValue(key, out int count))
            {
                if (count >= _burst) return false;
            }
            else if (_counts.Count >= _capacity)
            {
                return false;
            }
            _counts[key] = count + 1;
            return true;
        }
    }

    internal static AddressKey GetKey(IPAddress address)
    {
        ParsedIPAddress parsed = ParsedIPAddress.Parse(address);
        return new(parsed.Family, parsed.Family == IpFamily.IPv4 ? parsed.V4 : parsed.Hi);
    }

    internal readonly record struct AddressKey(IpFamily Family, ulong Prefix);
}
