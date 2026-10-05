// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions;

public static unsafe partial class Bytes
{
    /// <summary>
    /// Reverses the byte order of a 64-bit word.
    /// </summary>
    /// <remarks>
    /// Without Zbb, RISC-V has no byte-swap instruction, so the BCL's <c>ReverseEndianness</c> expands
    /// to a byte-at-a-time shuffle; <see cref="ZkEvmBitOperations.Bswap64"/> does it with three masked
    /// shift/or pairs on whole words, or with <c>rev8</c> in a guest whose zkVM has Zbb. See
    /// <c>Bytes.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Bswap64(ulong value) => ZkEvmBitOperations.Bswap64(value);

    /// <summary>
    /// Hoists whatever a run of <see cref="Bswap64"/> calls needs, so a caller pays for it once.
    /// </summary>
    /// <remarks>
    /// ILC re-forms the mask array's address for roughly every inlined use rather than once per
    /// method, so a run of swaps - the four limbs of a 256-bit word, times operands and result - pays
    /// for it repeatedly. Holding the masks in a local across the run costs one load each instead.
    /// See <c>Bytes.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Bswap64Hoist HoistBswap64()
    {
        ZkEvmBitOperations.LoadSwapMasks(out ulong m8, out ulong m16);
        return new Bswap64Hoist(m8, m16);
    }

    /// <summary>A run of byte swaps sharing the masks <see cref="HoistBswap64"/> loaded.</summary>
    internal readonly struct Bswap64Hoist(ulong m8, ulong m16)
    {
        private readonly ulong _m8 = m8;
        private readonly ulong _m16 = m16;

        /// <summary>Reverses the byte order of a 64-bit word.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong Bswap64(ulong value) => ZkEvmBitOperations.Swap(value, _m8, _m16);
    }

    /// <summary>Copies <paramref name="source"/> to the start of <paramref name="destination"/>.</summary>
    /// <remarks>
    /// Corelib copies more than 64 bytes through a GC-transition wrapper around the zkVM's <c>memmove</c>
    /// that spills every callee-saved register, ~60 steps a call whatever the length. Where
    /// <see cref="ZiskMemmoveFlag"/> is on, this calls <c>memmove</c> directly, so overlapping spans stay safe;
    /// elsewhere it keeps corelib's copy. See <c>Bytes.std.cs</c> for the host form.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="source"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Copy(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!ZiskMemmoveFlag.IsActive)
        {
            source.CopyTo(destination);
            return;
        }

        if ((uint)source.Length > (uint)destination.Length) ThrowDestinationTooShort();
        Memmove(
            Unsafe.AsPointer(ref MemoryMarshal.GetReference(destination)),
            Unsafe.AsPointer(ref MemoryMarshal.GetReference(source)),
            (nuint)source.Length);
    }

    /// <summary>The zkVM's <c>memmove</c>, which its runtime turns into a DMA precompile.</summary>
    /// <remarks>
    /// Raw pointers rather than <c>ref byte</c>: byref arguments make ILC wrap the call in a stub that pins
    /// both refs in a stack frame, ~10 steps a call. Unpinned pointers are safe because nothing between taking
    /// them and the call returning can reach a GC safepoint: the import suppresses the GC transition and the
    /// callee is a two-instruction thunk. Call only where <see cref="ZiskMemmoveFlag"/> is on.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Memmove(void* destination, void* source, nuint length) => MemmoveImport(destination, source, length);

    // Behind a wrapper so that a JIT compiling a caller, as the zkEVM test hosts do, never binds the import:
    // one that suppresses the GC transition is bound when it is compiled in, and the host has no such export.
    [DllImport("__Internal", EntryPoint = "memmove", ExactSpelling = true), SuppressGCTransition]
    private static extern void MemmoveImport(void* destination, void* source, nuint length);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowDestinationTooShort() => throw new ArgumentException("Destination is too short.", "destination");

    /// <summary>Compares the 32 bytes at <paramref name="a"/> with the 32 bytes at <paramref name="b"/>.</summary>
    /// <remarks>
    /// Four whole-word comparisons: the guest has no SIMD, and ILC expands a
    /// <see cref="System.Runtime.Intrinsics.Vector256{T}"/> comparison to a byte-at-a-time element loop
    /// over every lane. Loads are unaligned, so a caller may pass any byte offset.
    /// See <c>Bytes.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AreEqual32(ref byte a, ref byte b)
        => Unsafe.ReadUnaligned<ulong>(ref a) == Unsafe.ReadUnaligned<ulong>(ref b)
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 16)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16))
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 24)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24));

    /// <summary>Tests whether all 32 bytes at <paramref name="a"/> are zero.</summary>
    /// <remarks><inheritdoc cref="AreEqual32" path="/remarks"/></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsZero32(ref byte a)
        => (Unsafe.ReadUnaligned<ulong>(ref a)
            | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8))
            | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 16))
            | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 24))) == 0;

    /// <inheritdoc cref="LeadingZeroBytes"/>
    /// <remarks>There is no <c>clz</c> the toolchain will emit for this target, so
    /// <see cref="BitOperations.LeadingZeroCount(ulong)"/> reaches corelib's software fallback - a
    /// de Bruijn multiply, a table load and shifts, ~28 steps, and it ran 85,165 times over one block.
    /// Byte granularity needs only the comparison tree this switch lowers to.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroBytes(ulong value) => value switch
    {
        0 => sizeof(ulong),
        <= 0x0000_0000_0000_00FFUL => 7,
        <= 0x0000_0000_0000_FFFFUL => 6,
        <= 0x0000_0000_00FF_FFFFUL => 5,
        <= 0x0000_0000_FFFF_FFFFUL => 4,
        <= 0x0000_00FF_FFFF_FFFFUL => 3,
        <= 0x0000_FFFF_FFFF_FFFFUL => 2,
        <= 0x00FF_FFFF_FFFF_FFFFUL => 1,
        _ => 0,
    };

    /// <inheritdoc cref="LeadingZeroBits"/>
    /// <remarks>Built on <see cref="LeadingZeroBytes"/> rather than
    /// <see cref="BitOperations.LeadingZeroCount(ulong)"/> for the reason recorded there, then three
    /// more comparisons resolve the bits inside the leading non-zero byte.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroBits(ulong value)
    {
        int zeroBytes = LeadingZeroBytes(value);
        if (zeroBytes == sizeof(ulong)) return sizeof(ulong) * 8;

        return (zeroBytes << 3) + (byte)(value >> ((sizeof(ulong) - 1 - zeroBytes) << 3)) switch
        {
            <= 0x01 => 7,
            <= 0x03 => 6,
            <= 0x07 => 5,
            <= 0x0f => 4,
            <= 0x1f => 3,
            <= 0x3f => 2,
            <= 0x7f => 1,
            _ => 0,
        };
    }
}
