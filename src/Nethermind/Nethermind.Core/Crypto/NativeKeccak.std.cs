// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Crypto;

/// <summary>XKCP KeccakP-1600 opt64 rate-block absorb in a native library, used on x64 hosts with AVX2, BMI1 and BMI2 when the library loads.</summary>
internal static unsafe partial class NativeKeccak
{
    private const string LibraryName = "nmkeccak";

    /// <summary>Fewest rate blocks for which the native absorb is faster than the managed one; below it the call overhead dominates.</summary>
    public const int MinBlocks = 16;

    public static readonly bool IsAvailable = Avx2.IsSupported && Bmi1.X64.IsSupported && Bmi2.X64.IsSupported
        && NativeLibrary.TryLoad(LibraryName, typeof(NativeKeccak).Assembly, null, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Absorb(ref ulong state, ref byte input, int blocks)
    {
        fixed (ulong* s = &state)
        fixed (byte* d = &input)
            nm_keccak_absorb(s, d, (nuint)blocks);
    }

    [LibraryImport(LibraryName)]
    private static partial void nm_keccak_absorb(ulong* state, byte* data, nuint blocks);
}
