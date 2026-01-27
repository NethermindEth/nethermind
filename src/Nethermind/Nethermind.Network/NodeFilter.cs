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
/// for a short timeout window.
/// </summary>
/// <param name="size">
/// Maximum number of distinct <see cref="IpSubnetKey"/> entries retained. Once full, the underlying cache may evict
/// older entries, which can cause a previously seen address or subnet to be accepted again earlier than the timeout.
/// </param>
/// <param name="exactMatchOnly">
/// When <c>true</c>, disables subnet bucketing and always uses an exact-address key for <paramref name="ipAddress"/>
/// (IPv4-mapped IPv6 is still treated as IPv4). <paramref name="currentIp"/> is ignored for key selection.
/// </param>
/// <remarks>
/// Keying is delegated to <see cref="IpSubnetKey"/> unless <paramref name="exactMatchOnly"/> is enabled:
/// - Public addresses are typically bucketed by subnet (defaults: IPv4 /24, IPv6 /64) to reduce cache pressure and to
///   treat fast-rotating addresses within a single ISP or NAT range as the same source.
/// - Loopback, RFC1918, ULA, link-local, and CGNAT ranges are treated as exact addresses to avoid blocking large local networks.
/// - When <see cref="Set(IPAddress,IPAddress?)"/> is passed a non-null <c>currentIp</c>, keying uses
///   <see cref="IpSubnetKey.CreateNodeFilterKey"/> which can optionally treat same-local-subnet peers as exact.
///
/// This type is thread-safe. Although <c>ClockCache</c> is concurrency-safe, <see cref="Set(IPAddress,IPAddress?)"/> serialises
/// the check-and-update per lock-stripe to keep the "seen within timeout" decision consistent under concurrency.
/// </remarks>
public class NodeFilter(int size, bool exactMatchOnly)
{
    /// <summary>
    /// Duration within which a key is considered "recent" and will be rejected.
    /// </summary>
    private static readonly TimeSpan _timeOut = TimeSpan.FromMinutes(5);

    // Monotonic millisecond window derived once (avoids DateTime.UtcNow and wall-clock jumps).
    private static readonly long _timeOutMs = _timeOut.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// When true, always key by exact address (no subnet bucketing).
    /// </summary>
    private readonly bool _exactMatchOnly = exactMatchOnly;

    /// <summary>
    /// Cache mapping derived <see cref="IpSubnetKey"/> entries to a monotonic "last seen" timestamp
    /// (milliseconds since boot via <see cref="Environment.TickCount64"/>).
    /// </summary>
    private readonly ClockCache<IpSubnetKey, long> _nodesFilter = new(size);

    /// <summary>
    /// Checks whether <paramref name="ipAddress"/> should be accepted, based on whether the derived key has been seen recently.
    /// </summary>
    /// <param name="ipAddress">The remote address being evaluated.</param>
    /// <param name="currentIp">
    /// Optional local/current node address used to refine key selection when subnet bucketing is enabled.
    /// Ignored when constructed with <c>exactMatchOnly: true</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the derived key was not seen within the timeout window and was recorded as seen now.
    /// <c>false</c> if the derived key was seen within the timeout window.
    /// </returns>
    public bool Set(IPAddress ipAddress, IPAddress? currentIp)
    {
        // Monotonic "now" - avoids wall-clock adjustments affecting timeout behaviour.
        long now = Environment.TickCount64;

        IpSubnetKey key = _exactMatchOnly
            ? IpSubnetKey.Exact(ipAddress)
            : (currentIp is null
                ? IpSubnetKey.DefaultKey(ipAddress)
                : IpSubnetKey.CreateNodeFilterKey(ipAddress, currentIp));

        // Non-atomic branching; so under lock in case two requests come in at same time
        lock (_nodesFilter)
        {
            if (_nodesFilter.TryGet(key, out long lastSeen) && now - lastSeen < _timeOutMs)
                return false;

            _nodesFilter.Set(key, now);
            return true;
        }
    }

    /// <summary>
    /// Allocation-free key for an IP address or a masked subnet prefix, suitable for hash lookups and prefix checks.
    /// </summary>
    /// <remarks>
    /// Normalises IPv4 and IPv6 into a fixed-size (family, prefixBits, 128-bit prefix) representation.
    /// IPv4-mapped IPv6 (::ffff:a.b.c.d) is treated as IPv4.
    ///
    /// Use <see cref="Exact(IPAddress)"/> for per-address keys, or <see cref="Bucket(IPAddress,byte,byte)"/> to group by prefix.
    /// <see cref="DefaultKey(IPAddress,byte,byte)"/> and <see cref="CreateNodeFilterKey(IPAddress,IPAddress,byte,byte,byte,byte,bool,bool)"/>
    /// apply NodeFilter-oriented heuristics (treat local ranges as exact; optionally treat same-local-subnet peers as exact).
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    internal readonly struct IpSubnetKey : IEquatable<IpSubnetKey>
    {
        // For IPv6: _hi/_lo are the masked 128-bit network prefix (big-endian).
        // For IPv4: _hi holds the masked v4 in the low 32 bits (big-endian), _lo is 0.
        [FieldOffset(0)]
        private readonly ulong _hi;
        [FieldOffset(sizeof(ulong))]
        private readonly ulong _lo;
        // meta = (family << 8) | prefixBits
        // family is 4 or 6
        [FieldOffset(2 * sizeof(ulong))]
        private readonly ushort _meta;

        public static IpSubnetKey DefaultKey(IPAddress ipAddress, byte v4BucketPrefixBits = 24, byte v6BucketPrefixBits = 64)
        {
            byte family = ReadAddress(ipAddress, out uint v4, out ulong hi, out ulong lo);

            if (IsLoopbackOrPrivateOrLinkLocal(family, v4, hi, lo))
                return CreateExactFromParsed(family, v4, hi, lo);

            return family == 4
                ? CreateFromV4(v4, v4BucketPrefixBits)
                : CreateFromV6(hi, lo, v6BucketPrefixBits);
        }

        // Defaults:
        // - v4 buckets: /24
        // - v6 buckets: /64
        // IPv4-mapped IPv6 (::ffff:a.b.c.d) is treated as IPv4.
        public IpSubnetKey(IPAddress ipAddress, byte v4PrefixBits = 24, byte v6PrefixBits = 64)
        {
            byte family = ReadAddress(ipAddress, out uint v4, out ulong hi, out ulong lo);

            if (family == 4)
            {
                _meta = MakeMeta(4, v4PrefixBits);
                _hi = MaskV4(v4, v4PrefixBits);
                _lo = 0;
                return;
            }

            _meta = MakeMeta(6, v6PrefixBits);
            MaskV6(ref hi, ref lo, v6PrefixBits);
            _hi = hi;
            _lo = lo;
        }

        // Exact key - no subnet bucketing.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IpSubnetKey Exact(IPAddress ipAddress)
            => new(ipAddress, v4PrefixBits: 32, v6PrefixBits: 128);

        // Convenience - common bucket sizes.
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

        // Exact check: does this key's subnet contain the other IP?
        // No hashing involved - pure prefix-bit compare.
        public bool Matches(IPAddress other)
        {
            byte family = (byte)(_meta >> 8);
            byte prefix = (byte)_meta;

            byte otherFamily = ReadAddress(other, out uint v4, out ulong hi, out ulong lo);
            if (otherFamily != family)
                return false;

            if (family == 4)
                return _hi == MaskV4Trusted(v4, prefix);

            MaskV6Trusted(ref hi, ref lo, prefix);
            return _hi == hi && _lo == lo;
        }

        // Exact same-subnet check between two addresses, using the given prefix lengths.
        public static bool AreInSameSubnet(IPAddress a, IPAddress b, byte v4PrefixBits = 24, byte v6PrefixBits = 64)
        {
            byte fa = ReadAddress(a, out uint a4, out ulong aHi, out ulong aLo);
            byte fb = ReadAddress(b, out uint b4, out ulong bHi, out ulong bLo);

            if (fa != fb)
                return false;

            if (fa == 4)
                return MaskV4(a4, v4PrefixBits) == MaskV4(b4, v4PrefixBits);

            MaskV6(ref aHi, ref aLo, v6PrefixBits);
            MaskV6(ref bHi, ref bLo, v6PrefixBits);
            return aHi == bHi && aLo == bLo;
        }

        // Node-filter keying:
        // - If remote is loopback/private/ULA/link-local/CGNAT -> exact key
        // - Else if remote is in same subnet as currentIp -> exact key (by default only when currentIp is also local/private)
        // - Else -> bucket key
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
            byte rFamily = ReadAddress(remoteIp, out uint rV4, out ulong rHi, out ulong rLo);

            if (IsLoopbackOrPrivateOrLinkLocal(rFamily, rV4, rHi, rLo))
                return CreateExactFromParsed(rFamily, rV4, rHi, rLo);

            if (exactIfSameSubnetAsCurrentIp)
            {
                byte cFamily = ReadAddress(currentIp, out uint cV4, out ulong cHi, out ulong cLo);

                if (!requireCurrentIpIsLocalForExact || IsLoopbackOrPrivateOrLinkLocal(cFamily, cV4, cHi, cLo))
                {
                    if (rFamily == cFamily)
                    {
                        if (rFamily == 4)
                        {
                            ulong rNet = MaskV4(rV4, v4LocalPrefixBits);
                            ulong cNet = MaskV4(cV4, v4LocalPrefixBits);
                            if (rNet == cNet)
                                return CreateExactFromParsed(rFamily, rV4, rHi, rLo);
                        }
                        else
                        {
                            ulong rNetHi = rHi, rNetLo = rLo;
                            ulong cNetHi = cHi, cNetLo = cLo;
                            MaskV6(ref rNetHi, ref rNetLo, v6LocalPrefixBits);
                            MaskV6(ref cNetHi, ref cNetLo, v6LocalPrefixBits);

                            if (rNetHi == cNetHi && rNetLo == cNetLo)
                                return CreateExactFromParsed(rFamily, rV4, rHi, rLo);
                        }
                    }
                }
            }

            return rFamily == 4
                ? CreateFromV4(rV4, v4BucketPrefixBits)
                : CreateFromV6(rHi, rLo, v6BucketPrefixBits);
        }

        // Public helper if you want to branch outside CreateNodeFilterKey.
        public static bool IsLoopbackOrPrivateOrLinkLocal(IPAddress ip)
        {
            byte family = ReadAddress(ip, out uint v4, out ulong hi, out ulong lo);
            return IsLoopbackOrPrivateOrLinkLocal(family, v4, hi, lo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IpSubnetKey CreateExactFromParsed(byte family, uint v4, ulong hi, ulong lo)
            => family == 4 ? CreateFromV4(v4, 32) : CreateFromV6(hi, lo, 128);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IpSubnetKey CreateFromV4(uint v4, byte prefixBits)
            => new(MaskV4(v4, prefixBits), 0, MakeMeta(4, prefixBits));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IpSubnetKey CreateFromV6(ulong hi, ulong lo, byte prefixBits)
        {
            MaskV6(ref hi, ref lo, prefixBits);
            return new(hi, lo, MakeMeta(6, prefixBits));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IpSubnetKey(ulong hi, ulong lo, ushort meta)
        {
            _hi = hi;
            _lo = lo;
            _meta = meta;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort MakeMeta(byte family, byte prefixBits)
            => (ushort)((family << 8) | prefixBits);

        // Returns 4 for IPv4 or IPv4-mapped IPv6, 6 for IPv6.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ReadAddress(IPAddress ip, out uint v4, out ulong hi, out ulong lo)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!ip.TryWriteBytes(bytes, out int written))
                throw new ArgumentException("Invalid IPAddress.", nameof(ip));

            if (written == 4)
            {
                v4 = BinaryPrimitives.ReadUInt32BigEndian(bytes); // reads first 4 bytes
                hi = 0;
                lo = 0;
                return 4;
            }

            if (written == 16)
            {
                hi = BinaryPrimitives.ReadUInt64BigEndian(bytes);

                // Fast-path IPv4-mapped IPv6 (::ffff:a.b.c.d) - treat as IPv4.
                // Layout is 0:0:0:0:0:ffff:w.x.y.z
                if (hi == 0)
                {
                    uint mid = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4));
                    if (mid == 0x0000_FFFFu)
                    {
                        v4 = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(12, 4));
                        hi = 0;
                        lo = 0;
                        return 4;
                    }
                }

                v4 = 0;
                lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
                return 6;
            }

            throw new ArgumentException("Unsupported address length.", nameof(ip));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowV4PrefixOutOfRange()
            => throw new ArgumentOutOfRangeException("prefixBits", "IPv4 prefix must be 0..32.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowV6PrefixOutOfRange()
            => throw new ArgumentOutOfRangeException("prefixBits", "IPv6 prefix must be 0..128.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MaskV4(uint v4, byte prefixBits)
        {
            if (prefixBits > 32)
                ThrowV4PrefixOutOfRange();

            return MaskV4Trusted(v4, prefixBits);
        }

        // prefixBits must be 0..32 (trusted).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MaskV4Trusted(uint v4, byte prefixBits)
        {
            if (prefixBits == 0)
                return 0;

            uint mask = 0xFFFF_FFFFu << (32 - prefixBits); // prefixBits 1..32
            return (ulong)(v4 & mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaskV6(ref ulong hi, ref ulong lo, byte prefixBits)
        {
            if (prefixBits > 128)
                ThrowV6PrefixOutOfRange();

            MaskV6Trusted(ref hi, ref lo, prefixBits);
        }

        // prefixBits must be 0..128 (trusted).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaskV6Trusted(ref ulong hi, ref ulong lo, byte prefixBits)
        {
            if (prefixBits == 0)
            {
                hi = 0;
                lo = 0;
                return;
            }

            if (prefixBits < 64)
            {
                hi &= 0xFFFF_FFFF_FFFF_FFFFUL << (64 - prefixBits); // 1..63
                lo = 0;
                return;
            }

            if (prefixBits == 64)
            {
                lo = 0;
                return;
            }

            // 65..128 (128 keeps full address due to shift by 0)
            lo &= 0xFFFF_FFFF_FFFF_FFFFUL << (128 - prefixBits); // 63..0
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLoopbackOrPrivateOrLinkLocal(byte family, uint v4, ulong hi, ulong lo)
        {
            if (family == 4)
            {
                byte a = (byte)(v4 >> 24);
                byte b = (byte)(v4 >> 16);

                // Loopback: 127.0.0.0/8
                if (a == 127) return true;

                // RFC1918
                if (a == 10) return true;                              // 10.0.0.0/8
                if (a == 172 && (uint)(b - 16) <= 15u) return true;    // 172.16.0.0/12
                if (a == 192 && b == 168) return true;                 // 192.168.0.0/16

                // IPv4 link-local: 169.254.0.0/16
                if (a == 169 && b == 254) return true;

                // CGNAT: 100.64.0.0/10 (often behaves "private-ish" operationally)
                if (a == 100 && (b & 0xC0) == 0x40) return true;

                return false;
            }

            // IPv6 loopback: ::1
            if (hi == 0 && lo == 1)
                return true;

            byte first = (byte)(hi >> 56);
            byte second = (byte)(hi >> 48);

            // ULA: fc00::/7
            if ((first & 0xFE) == 0xFC)
                return true;

            // IPv6 link-local: fe80::/10
            if (first == 0xFE && (second & 0xC0) == 0x80)
                return true;

            return false;
        }
    }
}
