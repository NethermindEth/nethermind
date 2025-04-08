// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Caching;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Represents a filter that temporarily rejects repeated IP addresses within a specified time window.
/// </summary>
/// <param name="size">The maximum capacity of the underlying cache storing IP addresses and their timestamps.</param>
public class NodeFilter(int size)
{
    /// <summary>
    /// Defines the duration within which an IP address is considered "recent" 
    /// and will cause the filter to reject new attempts from that same IP.
    /// </summary>
    private static readonly TimeSpan _timeOut = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A clock-based cache that stores the timestamps for IP addresses.
    /// It is initialized with the specified size limit.
    /// </summary>
    private readonly ClockCache<IpAddressAsKey, DateTime> _nodesFilter = new(size);

    /// <summary>
    /// Attempts to set (or update) the specified IP address in the filter. If the IP address has been seen 
    /// within the timeout window, this method returns <c>false</c>. Otherwise, it updates the cache 
    /// with the current time and returns <c>true</c>.
    /// </summary>
    /// <param name="ipAddress">The IP address to check and insert/update in the filter.</param>
    /// <returns>
    /// <c>true</c> if the IP address was not found (or was outside the timeout window) 
    /// and was successfully inserted. <c>false</c> if the IP address was found 
    /// and is still within the timeout window.
    /// </returns>
    public bool Set(IPAddress ipAddress)
    {
        // Get the current UTC timestamp.
        DateTime now = DateTime.UtcNow;

        // Non-atomic branching; so under lock in case two requests come in at same time
        lock (_nodesFilter)
        {
            // Try to retrieve a previously recorded timestamp for the IP address.
            if (_nodesFilter.TryGet(ipAddress, out DateTime lastSeen) &&
                // Check if the last seen time is still within the timeout window.
                now - lastSeen < _timeOut)
            {
                // If yes, reject by returning false.
                return false;
            }
            else
            {
                // Otherwise, update (or add) the IP address timestamp to the current time.
                _nodesFilter.Set(ipAddress, now);
                return true;
            }
        }
    }

    private readonly struct IpAddressAsKey(IPAddress ipAddress) : IEquatable<IpAddressAsKey>
    {
        private readonly IPAddress _ipAddress = ipAddress;
        public static implicit operator IpAddressAsKey(IPAddress ip) => new(ip);
        public bool Equals(IpAddressAsKey other) => _ipAddress.Equals(other._ipAddress);
        public override bool Equals(object? obj) => obj is IpAddressAsKey ip && _ipAddress.Equals(ip._ipAddress);
        public override int GetHashCode() => _ipAddress.GetHashCode();
    }
}
