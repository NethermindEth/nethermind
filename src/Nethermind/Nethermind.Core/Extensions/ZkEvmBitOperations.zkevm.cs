// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    // xxHash64 prime — good avalanche when folded against the high bits. Frozen-array load, not a
    // literal: the riscv64 backend materializes 64-bit constants with five-instruction sequences.
    private static readonly ulong[] PrimeConstant = [0xD6E8FEB86659FD93UL];

    // The swap masks live in a frozen array: as literals, the riscv64 backend materializes each
    // 64-bit constant with a five-instruction sequence at every inlined use, which the profile shows
    // on every stack word swap. An element load is two instructions and cannot be folded back.
    private static readonly ulong[] SwapMasks = [0x00FF00FF00FF00FFUL, 0x0000FFFF0000FFFFUL];

    // RISC-V has no byte-swap instruction; this all-64-bit form beats the BCL's ReverseEndianness.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong x)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        return Swap(x, masks, Unsafe.Add(ref masks, 1));
    }

    /// <summary>Writes <paramref name="value"/> to <paramref name="destination"/> with all 32 bytes reversed.</summary>
    /// <remarks>Shares the swap masks across the four lanes and stores lanes directly; per-lane
    /// <see cref="Bswap64"/> calls rematerialize the mask constants for every lane, and composing the
    /// result through <see cref="Vector256"/> round-trips it through memory. Lanes are stored as they
    /// are computed, so <paramref name="destination"/> must not overlap <paramref name="value"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bswap256(in UInt256 value, ref Vector256<byte> destination)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        ulong m8 = masks;
        ulong m16 = Unsafe.Add(ref masks, 1);
        ref ulong d = ref Unsafe.As<Vector256<byte>, ulong>(ref destination);
        d = Swap(value.u3, m8, m16);
        Unsafe.Add(ref d, 1) = Swap(value.u2, m8, m16);
        Unsafe.Add(ref d, 2) = Swap(value.u1, m8, m16);
        Unsafe.Add(ref d, 3) = Swap(value.u0, m8, m16);
    }

    /// <summary>Reads 32 bytes at <paramref name="source"/> reversed into <paramref name="result"/>.</summary>
    /// <remarks><inheritdoc cref="Bswap256(in UInt256, ref Vector256{byte})" path="/remarks"/></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bswap256(ref readonly byte source, out UInt256 result)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        ulong m8 = masks;
        ulong m16 = Unsafe.Add(ref masks, 1);
        ref byte s = ref Unsafe.AsRef(in source);
        Unsafe.SkipInit(out result);
        ref ulong r = ref Unsafe.As<UInt256, ulong>(ref result);
        Unsafe.Add(ref r, 3) = Swap(Unsafe.ReadUnaligned<ulong>(ref s), m8, m16);
        Unsafe.Add(ref r, 2) = Swap(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, 8)), m8, m16);
        Unsafe.Add(ref r, 1) = Swap(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, 16)), m8, m16);
        r = Swap(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, 24)), m8, m16);
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
        ulong x = (crc ^ data) * MemoryMarshal.GetArrayDataReference(PrimeConstant);
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
