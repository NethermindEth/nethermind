// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

internal static partial class ByteSwap
{
    /// <summary>Reverses the byte order of a 64-bit word.</summary>
    /// <remarks>On the host this is a single <c>bswap</c> / <c>rev</c>, so defer to the intrinsic.
    /// See <c>ByteSwap.zkevm.cs</c> for the guest form and why it differs.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Reverse(ulong value) => BinaryPrimitives.ReverseEndianness(value);

    /// <summary>Hoists whatever a run of swaps needs to load, so a caller pays for it once.</summary>
    /// <remarks>Nothing to hoist on the host; the empty struct disappears when inlined.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Swapper Hoist() => default;

    /// <inheritdoc cref="Hoist"/>
    internal readonly struct Swapper
    {
        /// <inheritdoc cref="ByteSwap.Reverse(ulong)"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Reverse(ulong value) => BinaryPrimitives.ReverseEndianness(value);
    }
}
