// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;

namespace Nethermind.Network;

internal enum IpFamily : byte { IPv4 = 4, IPv6 = 6 }

internal readonly struct ParsedIPAddress(IpFamily family, uint v4, ulong hi, ulong lo)
{
    public readonly IpFamily Family = family;
    public readonly uint V4 = v4;
    public readonly ulong Hi = hi;
    public readonly ulong Lo = lo;

    internal static ParsedIPAddress Parse(IPAddress ipAddress)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);

        Span<byte> bytes = stackalloc byte[16];
        if (!ipAddress.TryWriteBytes(bytes, out int written))
        {
            throw new ArgumentException("Invalid IPAddress.", nameof(ipAddress));
        }

        switch (written)
        {
            case 4:
                return new ParsedIPAddress(
                    IpFamily.IPv4,
                    BinaryPrimitives.ReadUInt32BigEndian(bytes),
                    hi: 0,
                    lo: 0);
            case 16:
                {
                    ulong hi = BinaryPrimitives.ReadUInt64BigEndian(bytes);

                    if (hi == 0)
                    {
                        uint mid = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4));
                        if (mid == 0x0000_FFFFu)
                        {
                            return new ParsedIPAddress(
                                IpFamily.IPv4,
                                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(12, 4)),
                                hi: 0,
                                lo: 0);
                        }
                    }

                    return new ParsedIPAddress(
                        IpFamily.IPv6,
                        v4: 0,
                        hi,
                        BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8)));
                }
            default:
                throw new ArgumentException("Unsupported address length.", nameof(ipAddress));
        }
    }

    public bool IsLoopbackOrPrivateOrLinkLocal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Family == IpFamily.IPv4
            ? IsIPv4LoopbackOrPrivateOrLinkLocal(V4)
            : IsIPv6LoopbackOrPrivateOrLinkLocal(Hi, Lo);
    }

    public bool IsMulticast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Family == IpFamily.IPv4
            ? IsIPv4MulticastAddress(V4)
            : IsIPv6Multicast(Hi);
    }

    public bool IsIPv4Multicast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Family == IpFamily.IPv4 && IsIPv4MulticastAddress(V4);
    }

    public bool IsSpecialUseAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Family == IpFamily.IPv4
            ? IsIPv4SpecialUseAddress(V4)
            : IsIPv6SpecialUseAddress(Hi, Lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIPv4MulticastAddress(uint v4)
        => (byte)(v4 >> 24) is >= 224 and <= 239;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIPv4LoopbackOrPrivateOrLinkLocal(uint v4)
    {
        byte a = (byte)(v4 >> 24);
        byte b = (byte)(v4 >> 16);

        return a == 127                            // Loopback: 127.0.0.0/8
               || a == 10                          // RFC1918: 10.0.0.0/8
               || a == 172 && (uint)(b - 16) <= 15u // RFC1918: 172.16.0.0/12
               || a == 192 && b == 168             // RFC1918: 192.168.0.0/16
               || a == 169 && b == 254             // IPv4 link-local: 169.254.0.0/16
               || a == 100 && (b & 0xC0) == 0x40;  // CGNAT: 100.64.0.0/10
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIPv6LoopbackOrPrivateOrLinkLocal(ulong hi, ulong lo)
    {
        if (hi == 0 && lo == 1)
        {
            return true;
        }

        byte first = (byte)(hi >> 56);
        byte second = (byte)(hi >> 48);

        return (first & 0xFE) == 0xFC                      // ULA: fc00::/7
               || first == 0xFE && (second & 0xC0) == 0x80; // IPv6 link-local: fe80::/10
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIPv4SpecialUseAddress(uint v4)
    {
        byte a = (byte)(v4 >> 24);
        byte b = (byte)(v4 >> 16);
        byte c = (byte)(v4 >> 8);

        return a == 0                              // 0.0.0.0/8
               || a == 192 && b == 0 && c is 0 or 2 // 192.0.0.0/24, 192.0.2.0/24
               || a == 192 && b == 31 && c == 196   // 192.31.196.0/24
               || a == 192 && b == 52 && c == 193   // 192.52.193.0/24
               || a == 192 && b == 88 && c == 99    // 192.88.99.0/24
               || a == 192 && b == 175 && c == 48   // 192.175.48.0/24
               || a == 198 && b is 18 or 19         // 198.18.0.0/15
               || a == 198 && b == 51 && c == 100   // 198.51.100.0/24
               || a == 203 && b == 0 && c == 113    // 203.0.113.0/24
               || a >= 224;                         // 224.0.0.0/4, 240.0.0.0/4
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIPv6SpecialUseAddress(ulong hi, ulong lo)
    {
        byte b0 = (byte)(hi >> 56);
        byte b1 = (byte)(hi >> 48);
        byte b2 = (byte)(hi >> 40);
        byte b3 = (byte)(hi >> 32);
        byte b4 = (byte)(hi >> 24);
        byte b5 = (byte)(hi >> 16);

        return b0 == 0x00 && b1 == 0x64 && b2 == 0xff && b3 == 0x9b && IsZeroFromByte4To11(hi, lo) // 64:ff9b::/96
               || b0 == 0x00 && b1 == 0x64 && b2 == 0xff && b3 == 0x9b && b4 == 0x00 && b5 == 0x01 // 64:ff9b:1::/48
               || b0 == 0x01 && (hi & 0x00FF_FFFF_FFFF_FFFFUL) == 0 // 100::/64
               || b0 == 0x20 && b1 == 0x01 && (b2 & 0xfe) == 0x00 // 2001::/23
               || b0 == 0x20 && b1 == 0x01 && b2 == 0x0d && b3 == 0xb8 // 2001:db8::/32
               || b0 == 0x20 && b1 == 0x02 // 2002::/16
               || b0 == 0x3f && b1 == 0xff && (b2 & 0xf0) == 0x00; // 3fff::/20
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIPv6Multicast(ulong hi)
        => (byte)(hi >> 56) == 0xff;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsZeroFromByte4To11(ulong hi, ulong lo)
        => (hi & 0x0000_0000_FFFF_FFFFUL) == 0
           && (lo & 0xFFFF_FFFF_0000_0000UL) == 0;
}
