// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Crypto;

/// <summary>XKCP KeccakP-1600 opt64 rate-block absorb in a native library, used on x64 hosts with AVX2, BMI1 and BMI2 when the library loads.</summary>
internal static unsafe class NativeKeccak
{
    private const string LibraryName = "nmkeccak";
    private const string ExportName = "nm_keccak_absorb";

    /// <summary>The configured number of rate blocks from which the native absorb is used instead of the managed one.</summary>
    public const int MinBlocks = 16;

    private static readonly delegate* unmanaged<ulong*, byte*, nuint, void> _absorb = Resolve();

    public static readonly bool IsAvailable = _absorb is not null;

    private static delegate* unmanaged<ulong*, byte*, nuint, void> Resolve()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || !Avx2.IsSupported || !Bmi1.X64.IsSupported || !Bmi2.X64.IsSupported)
            return null;

        if (!NativeLibrary.TryLoad(LibraryName, typeof(NativeKeccak).Assembly, null, out nint handle))
            return null;

        if (!NativeLibrary.TryGetExport(handle, ExportName, out nint export))
        {
            NativeLibrary.Free(handle);
            return null;
        }

        return (delegate* unmanaged<ulong*, byte*, nuint, void>)export;
    }

    /// <summary>Absorbs <paramref name="blocks"/> rate blocks of <paramref name="input"/> into <paramref name="state"/>.</summary>
    /// <remarks>The native call reads and writes through raw pointers and checks nothing. The caller must ensure that
    /// <paramref name="state"/> is the first of 25 writable lanes and that <paramref name="input"/> is the first of at least
    /// <c>blocks * 136</c> readable bytes. <see cref="IsAvailable"/> must be <see langword="true"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Absorb(ref ulong state, ref byte input, int blocks)
    {
        fixed (ulong* s = &state)
        fixed (byte* d = &input)
            _absorb(s, d, (nuint)blocks);
    }
}
