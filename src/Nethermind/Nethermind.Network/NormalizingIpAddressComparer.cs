// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Network;

/// <summary>
/// Compares <see cref="IPAddress"/> values treating an IPv4-mapped IPv6 address as equal to its plain IPv4 form,
/// so both representations of the same address hash to the same bucket and compare equal.
/// </summary>
/// <remarks>
/// Each address is reduced to a 128-bit <see cref="Vector128{T}"/> key, so comparison is a single SIMD equality
/// and nothing is allocated.
/// </remarks>
public sealed class NormalizingIpAddressComparer : IEqualityComparer<IPAddress>
{
    public static readonly NormalizingIpAddressComparer Instance = new();

    private NormalizingIpAddressComparer() { }

    public bool Equals(IPAddress? x, IPAddress? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return Normalize(x) == Normalize(y);
    }

    public int GetHashCode(IPAddress obj) => Normalize(obj).GetHashCode();

    private static Vector128<byte> Normalize(IPAddress ip)
    {
        Vector128<byte> vector = default;
        Span<byte> bytes = MemoryMarshal.CreateSpan(ref Unsafe.As<Vector128<byte>, byte>(ref vector), Vector128<byte>.Count);
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // Fold IPv4 up into its ::ffff: mapped form (zeroed prefix already in place) so it matches the mapped
            // representation, while a genuine IPv4-compatible IPv6 stays distinct.
            bytes[10] = bytes[11] = 0xff;
            ip.TryWriteBytes(bytes[12..], out _);
        }
        else
        {
            ip.TryWriteBytes(bytes, out _);
        }

        return vector;
    }
}
