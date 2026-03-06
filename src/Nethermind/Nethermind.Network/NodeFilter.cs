// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;

namespace Nethermind.Network;

/// <summary>
/// Capacity-bounded filter that rejects repeat connection attempts from the same address (or address bucket)
/// for a short timeout window. Use <see cref="AcceptAll"/> when filtering is disabled.
/// </summary>
public sealed class NodeFilter
{
    private static readonly long DefaultTimeoutMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    /// <summary>
    /// A filter that always accepts every address. Used when filtering is disabled.
    /// </summary>
    public static readonly NodeFilter AcceptAll = new();

    private readonly ClockCache<IpSubnetKey, long>? _cache;
    private readonly bool _exactMatchOnly;
    private readonly IpSubnetKey.ParsedIp? _parsedCurrentIp;
    private readonly long _timeoutMs;

    private NodeFilter() { }

    public NodeFilter(int size, bool exactMatchOnly, IPAddress? currentIp)
        : this(size, exactMatchOnly, currentIp, DefaultTimeoutMs) { }

    internal NodeFilter(int size, bool exactMatchOnly, IPAddress? currentIp, long timeoutMs)
    {
        _cache = new(size);
        _exactMatchOnly = exactMatchOnly;
        _parsedCurrentIp = currentIp is not null ? new IpSubnetKey.ParsedIp(currentIp) : null;
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Creates a <see cref="NodeFilter"/> from common parameters, returning <see cref="AcceptAll"/> when disabled.
    /// </summary>
    public static NodeFilter Create(int maxActivePeers, bool filterEnabled, bool subnetBucketing, IPAddress? currentIp)
        => filterEnabled ? new NodeFilter(maxActivePeers * 4, !subnetBucketing, currentIp) : AcceptAll;

    /// <summary>
    /// Checks whether <paramref name="ipAddress"/> should be accepted.
    /// Returns <c>true</c> if the address was not seen recently, <c>false</c> if it was.
    /// </summary>
    public bool TryAccept(IPAddress ipAddress, bool exactOnly = false)
    {
        if (_cache is null) return true;

        long now = Environment.TickCount64;
        IpSubnetKey key = GetKey(ipAddress, exactOnly);

        // Benign race: two threads may both accept the same key concurrently.
        // The filter is advisory — a double-accept is harmless.
        if (_cache.TryGet(key, out long lastSeen) && now - lastSeen < _timeoutMs)
            return false;

        _cache.Set(key, now);
        return true;
    }

    internal void Touch(IPAddress ipAddress, bool exactOnly = false)
    {
        if (_cache is null)
        {
            return;
        }

        _cache.Set(GetKey(ipAddress, exactOnly), Environment.TickCount64);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IpSubnetKey GetKey(IPAddress ipAddress, bool exactOnly)
        => _exactMatchOnly || exactOnly
            ? IpSubnetKey.Exact(ipAddress)
            : (_parsedCurrentIp is { } current
                ? IpSubnetKey.CreateNodeFilterKey(ipAddress, current)
                : IpSubnetKey.DefaultKey(ipAddress));

    /// <summary>
    /// Allocation-free key for an IP address or a masked subnet prefix, suitable for hash lookups and prefix checks.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal readonly struct IpSubnetKey : IEquatable<IpSubnetKey>
    {
        internal enum IpFamily : byte { IPv4 = 4, IPv6 = 6 }

        internal readonly struct ParsedIp
        {
            public readonly IpFamily Family;
            public readonly uint V4;
            public readonly ulong Hi;
            public readonly ulong Lo;
            public readonly bool IsLocal;

            public ParsedIp(IPAddress ip)
            {
                Family = ReadAddress(ip, out uint v4, out ulong hi, out ulong lo);
                V4 = v4;
                Hi = hi;
                Lo = lo;
                IsLocal = IsLoopbackOrPrivateOrLinkLocal(Family, v4, hi, lo);
            }
        }

        // For IPv6: _hi/_lo are the masked 128-bit network prefix (big-endian).
        // For IPv4: _hi holds the masked v4 in the low 32 bits (big-endian), _lo is 0.
        [FieldOffset(0)]
        private readonly ulong _hi;
        [FieldOffset(sizeof(ulong))]
        private readonly ulong _lo;
        // meta = (family << 8) | prefixBits
        [FieldOffset(2 * sizeof(ulong))]
        private readonly ushort _meta;

        public static IpSubnetKey DefaultKey(IPAddress ipAddress, byte v4BucketPrefixBits = 24, byte v6BucketPrefixBits = 64)
        {
            IpFamily family = ReadAddress(ipAddress, out uint v4, out ulong hi, out ulong lo);

            if (IsLoopbackOrPrivateOrLinkLocal(family, v4, hi, lo))
            {
                v4BucketPrefixBits = 32;
                v6BucketPrefixBits = 128;
            }

            return family == IpFamily.IPv4
                ? CreateFromV4(v4, v4BucketPrefixBits)
                : CreateFromV6(hi, lo, v6BucketPrefixBits);
        }

        public IpSubnetKey(IPAddress ipAddress, byte v4PrefixBits = 24, byte v6PrefixBits = 64)
        {
            IpFamily family = ReadAddress(ipAddress, out uint v4, out ulong hi, out ulong lo);

            if (family == IpFamily.IPv4)
            {
                _meta = MakeMeta(IpFamily.IPv4, v4PrefixBits);
                _hi = MaskV4(v4, v4PrefixBits);
                _lo = 0;
                return;
            }

            _meta = MakeMeta(IpFamily.IPv6, v6PrefixBits);
            MaskV6(ref hi, ref lo, v6PrefixBits);
            _hi = hi;
            _lo = lo;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IpSubnetKey Exact(IPAddress ipAddress)
            => new(ipAddress, v4PrefixBits: 32, v6PrefixBits: 128);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IpSubnetKey Bucket(IPAddress ipAddress, byte v4PrefixBits = 24, byte v6PrefixBits = 64)
            => new(ipAddress, v4PrefixBits, v6PrefixBits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(IpSubnetKey other)
            => _meta == other._meta && _hi == other._hi && _lo == other._lo;

        public override bool Equals(object? obj)
            => obj is IpSubnetKey other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(IpSubnetKey left, IpSubnetKey right) => left.Equals(right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(IpSubnetKey left, IpSubnetKey right) => !left.Equals(right);

        public override int GetHashCode()
            => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<ulong, byte>(ref Unsafe.AsRef(in _hi)), 2 * sizeof(ulong) + sizeof(ushort)).FastHash();

        public bool Matches(IPAddress other)
        {
            IpFamily family = (IpFamily)(_meta >> 8);
            byte prefix = (byte)_meta;

            IpFamily otherFamily = ReadAddress(other, out uint v4, out ulong hi, out ulong lo);
            if (otherFamily != family)
                return false;

            if (family == IpFamily.IPv4)
                return _hi == MaskV4Trusted(v4, prefix);

            MaskV6Trusted(ref hi, ref lo, prefix);
            return _hi == hi && _lo == lo;
        }

        public static bool AreInSameSubnet(IPAddress a, IPAddress b, byte v4PrefixBits = 24, byte v6PrefixBits = 64)
        {
            IpFamily fa = ReadAddress(a, out uint a4, out ulong aHi, out ulong aLo);
            IpFamily fb = ReadAddress(b, out uint b4, out ulong bHi, out ulong bLo);

            if (fa != fb)
                return false;

            if (fa == IpFamily.IPv4)
                return MaskV4(a4, v4PrefixBits) == MaskV4(b4, v4PrefixBits);

            MaskV6(ref aHi, ref aLo, v6PrefixBits);
            MaskV6(ref bHi, ref bLo, v6PrefixBits);
            return aHi == bHi && aLo == bLo;
        }

        public static IpSubnetKey CreateNodeFilterKey(
            IPAddress remoteIp,
            IPAddress currentIp,
            byte v4BucketPrefixBits = 24,
            byte v6BucketPrefixBits = 64,
            byte v4LocalPrefixBits = 24,
            byte v6LocalPrefixBits = 64,
            bool exactIfSameSubnetAsCurrentIp = true,
            bool requireCurrentIpIsLocalForExact = true)
        {
            ParsedIp current = new(currentIp);
            return CreateNodeFilterKey(remoteIp, current,
                v4BucketPrefixBits, v6BucketPrefixBits,
                v4LocalPrefixBits, v6LocalPrefixBits,
                exactIfSameSubnetAsCurrentIp, requireCurrentIpIsLocalForExact);
        }

        public static IpSubnetKey CreateNodeFilterKey(
            IPAddress remoteIp,
            ParsedIp currentIp,
            byte v4BucketPrefixBits = 24,
            byte v6BucketPrefixBits = 64,
            byte v4LocalPrefixBits = 24,
            byte v6LocalPrefixBits = 64,
            bool exactIfSameSubnetAsCurrentIp = true,
            bool requireCurrentIpIsLocalForExact = true)
        {
            IpFamily rFamily = ReadAddress(remoteIp, out uint rV4, out ulong rHi, out ulong rLo);

            if (IsLoopbackOrPrivateOrLinkLocal(rFamily, rV4, rHi, rLo))
                return CreateExactFromParsed(rFamily, rV4, rHi, rLo);

            if (exactIfSameSubnetAsCurrentIp)
            {
                if (!requireCurrentIpIsLocalForExact || currentIp.IsLocal)
                {
                    if (rFamily == currentIp.Family)
                    {
                        if (rFamily == IpFamily.IPv4)
                        {
                            if (MaskV4(rV4, v4LocalPrefixBits) == MaskV4(currentIp.V4, v4LocalPrefixBits))
                                return CreateExactFromParsed(rFamily, rV4, rHi, rLo);
                        }
                        else
                        {
                            ulong rNetHi = rHi, rNetLo = rLo;
                            ulong cNetHi = currentIp.Hi, cNetLo = currentIp.Lo;
                            MaskV6(ref rNetHi, ref rNetLo, v6LocalPrefixBits);
                            MaskV6(ref cNetHi, ref cNetLo, v6LocalPrefixBits);

                            if (rNetHi == cNetHi && rNetLo == cNetLo)
                                return CreateExactFromParsed(rFamily, rV4, rHi, rLo);
                        }
                    }
                }
            }

            return rFamily == IpFamily.IPv4
                ? CreateFromV4(rV4, v4BucketPrefixBits)
                : CreateFromV6(rHi, rLo, v6BucketPrefixBits);
        }

        public static bool IsLoopbackOrPrivateOrLinkLocal(IPAddress ip)
        {
            IpFamily family = ReadAddress(ip, out uint v4, out ulong hi, out ulong lo);
            return IsLoopbackOrPrivateOrLinkLocal(family, v4, hi, lo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IpSubnetKey CreateExactFromParsed(IpFamily family, uint v4, ulong hi, ulong lo)
            => family == IpFamily.IPv4 ? CreateFromV4(v4, 32) : CreateFromV6(hi, lo, 128);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IpSubnetKey CreateFromV4(uint v4, byte prefixBits)
            => new(MaskV4(v4, prefixBits), 0, MakeMeta(IpFamily.IPv4, prefixBits));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IpSubnetKey CreateFromV6(ulong hi, ulong lo, byte prefixBits)
        {
            MaskV6(ref hi, ref lo, prefixBits);
            return new(hi, lo, MakeMeta(IpFamily.IPv6, prefixBits));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IpSubnetKey(ulong hi, ulong lo, ushort meta)
        {
            _hi = hi;
            _lo = lo;
            _meta = meta;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort MakeMeta(IpFamily family, byte prefixBits)
            => (ushort)(((byte)family << 8) | prefixBits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IpFamily ReadAddress(IPAddress ip, out uint v4, out ulong hi, out ulong lo)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!ip.TryWriteBytes(bytes, out int written))
                throw new ArgumentException("Invalid IPAddress.", nameof(ip));

            switch (written)
            {
                case 4:
                    v4 = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                    hi = 0;
                    lo = 0;
                    return IpFamily.IPv4;
                case 16:
                    {
                        hi = BinaryPrimitives.ReadUInt64BigEndian(bytes);

                        // Fast-path IPv4-mapped IPv6 (::ffff:a.b.c.d) - treat as IPv4.
                        if (hi == 0)
                        {
                            uint mid = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4));
                            if (mid == 0x0000_FFFFu)
                            {
                                v4 = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(12, 4));
                                hi = 0;
                                lo = 0;
                                return IpFamily.IPv4;
                            }
                        }

                        v4 = 0;
                        lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
                        return IpFamily.IPv6;
                    }
                default:
                    throw new ArgumentException("Unsupported address length.", nameof(ip));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MaskV4(uint v4, byte prefixBits)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixBits, (byte)32, nameof(prefixBits));
            return MaskV4Trusted(v4, prefixBits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MaskV4Trusted(uint v4, byte prefixBits)
            => prefixBits == 0 ? 0UL : v4 & (0xFFFF_FFFFu << (32 - prefixBits));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaskV6(ref ulong hi, ref ulong lo, byte prefixBits)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixBits, (byte)128, nameof(prefixBits));
            MaskV6Trusted(ref hi, ref lo, prefixBits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaskV6Trusted(ref ulong hi, ref ulong lo, byte prefixBits)
        {
            switch (prefixBits)
            {
                case 0:
                    hi = 0;
                    lo = 0;
                    return;
                case < 64:
                    hi &= 0xFFFF_FFFF_FFFF_FFFFUL << (64 - prefixBits);
                    lo = 0;
                    return;
                case 64:
                    lo = 0;
                    return;
                default:
                    // 65..128 (128 keeps full address due to shift by 0)
                    lo &= 0xFFFF_FFFF_FFFF_FFFFUL << (128 - prefixBits);
                    return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLoopbackOrPrivateOrLinkLocal(IpFamily family, uint v4, ulong hi, ulong lo)
        {
            if (family == IpFamily.IPv4)
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

            // IPv6 loopback: ::1
            if (hi == 0 && lo == 1)
                return true;

            byte first = (byte)(hi >> 56);
            byte second = (byte)(hi >> 48);

            return (first & 0xFE) == 0xFC                  // ULA: fc00::/7
                   || first == 0xFE && (second & 0xC0) == 0x80; // IPv6 link-local: fe80::/10
        }
    }
}
