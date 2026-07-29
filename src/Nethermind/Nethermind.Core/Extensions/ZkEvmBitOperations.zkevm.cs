// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;

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
