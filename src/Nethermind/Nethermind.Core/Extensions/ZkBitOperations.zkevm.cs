// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions;

/// <summary>
/// ZisK (RISC-V) substitute for the CRC32C / rotate primitives that
/// <see cref="SpanExtensions"/> uses to build in-memory hashes. RISC-V has no
/// CRC32 instruction, so the BCL's <c>BitOperations.Crc32C</c> degrades to a
/// slow per-word software loop — the dominant cost of every
/// <c>Dictionary</c>/<c>FrozenSet</c> lookup keyed by an address or hash.
///
/// The substitute is a multiply-fold mix that lowers to a single hardware MUL.
/// The result is NOT CRC32C; it is only used for ephemeral <c>GetHashCode</c>
/// values (never persisted, never put on the wire), so any well-distributed
/// hash is correct. Aliased in via <c>using BitOperations = ZkBitOperations</c>
/// under <c>#if ZK_EVM</c>.
/// </summary>
internal static class ZkBitOperations
{
    // xxHash64 prime — good avalanche when paired with a high-bit fold.
    private const ulong Prime = 0xD6E8FEB86659FD93UL;

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
    public static uint RotateLeft(uint value, int offset) =>
        System.Numerics.BitOperations.RotateLeft(value, offset);
}
