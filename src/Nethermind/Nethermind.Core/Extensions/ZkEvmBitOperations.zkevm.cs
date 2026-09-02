// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

/// <summary>
/// RISC-V (zkVM) substitute for the <see cref="BitOperations"/> primitives
/// <see cref="SpanExtensions"/> uses for in-memory hashing. RISC-V lacks a CRC32
/// instruction, so the BCL's <c>Crc32C</c> falls back to a slow software loop.
/// </summary>
/// <remarks>
/// The replacement is a multiply-fold that lowers to one hardware MUL. It is not
/// CRC32C, but the output only feeds ephemeral <c>GetHashCode</c> values (never
/// persisted or sent over the wire), so any well-distributed hash suffices.
/// </remarks>
public static partial class ZkEvmBitOperations
{
    // xxHash64 prime — good avalanche when folded against the high bits.
    private const ulong Prime = 0xD6E8FEB86659FD93UL;

    // RISC-V has no byte-swap instruction; this all-64-bit form beats the BCL's ReverseEndianness.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong x)
    {
        x = ((x & 0x00FF00FF00FF00FFUL) << 8) | ((x >> 8) & 0x00FF00FF00FF00FFUL);
        x = ((x & 0x0000FFFF0000FFFFUL) << 16) | ((x >> 16) & 0x0000FFFF0000FFFFUL);
        return (x << 32) | (x >> 32);
    }

    /// <summary>Writes <paramref name="value"/> to <paramref name="destination"/> with all 32 bytes reversed.</summary>
    /// <remarks>Shares the swap masks across the four lanes and stores lanes directly; per-lane
    /// <see cref="Bswap64"/> calls rematerialize the mask constants for every lane, and composing the
    /// result through <see cref="Vector256"/> round-trips it through memory.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bswap256(in UInt256 value, ref Vector256<byte> destination)
    {
        ulong m8 = 0x00FF00FF00FF00FFUL;
        ulong m16 = 0x0000FFFF0000FFFFUL;
        ref ulong d = ref Unsafe.As<Vector256<byte>, ulong>(ref destination);
        d = Swap(value.u3, m8, m16);
        Unsafe.Add(ref d, 1) = Swap(value.u2, m8, m16);
        Unsafe.Add(ref d, 2) = Swap(value.u1, m8, m16);
        Unsafe.Add(ref d, 3) = Swap(value.u0, m8, m16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Swap(ulong x, ulong m8, ulong m16)
    {
        x = ((x & m8) << 8) | ((x >> 8) & m8);
        x = ((x & m16) << 16) | ((x >> 16) & m16);
        return (x << 32) | (x >> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Crc32C(uint crc, ulong data)
    {
        ulong x = (crc ^ data) * Prime;
        return (uint)(x ^ (x >> 29));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Crc32C(uint crc, uint data) => Crc32C(crc, (ulong)data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Crc32C(uint crc, ushort data) => Crc32C(crc, (ulong)data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Crc32C(uint crc, byte data) => Crc32C(crc, (ulong)data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RotateLeft(uint value, int offset) => BitOperations.RotateLeft(value, offset);
}
