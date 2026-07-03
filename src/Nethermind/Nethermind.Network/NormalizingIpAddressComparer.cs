// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;

namespace Nethermind.Network;

/// <summary>
/// Compares <see cref="IPAddress"/> values treating an IPv4-mapped IPv6 address as equal to its plain IPv4 form,
/// so both representations of the same address hash to the same bucket and compare equal.
/// </summary>
/// <remarks>
/// Lets IP-keyed collections match regardless of how the peer's address happens to be represented, without
/// callers having to normalize before every lookup.
/// </remarks>
public sealed class NormalizingIpAddressComparer : IEqualityComparer<IPAddress>
{
    public static readonly NormalizingIpAddressComparer Instance = new();

    private NormalizingIpAddressComparer() { }

    public bool Equals(IPAddress? x, IPAddress? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && Normalize(x).Equals(Normalize(y)));

    public int GetHashCode(IPAddress obj) => Normalize(obj).GetHashCode();

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
