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
}
