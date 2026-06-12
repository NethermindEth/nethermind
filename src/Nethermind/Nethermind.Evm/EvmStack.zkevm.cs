// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <remarks>
    /// RISC-V has no byte-swap instruction so <c>ReverseEndianness</c> is a software shuffle;
    /// stack words produced by PUSH0/PUSH1/PUSH2 etc. have a zero high 24 bytes, so the
    /// common case only swaps the low limb instead of all four.
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial UInt256 ReadBeWord(ref byte bytes)
    {
        ulong r0 = Unsafe.ReadUnaligned<ulong>(ref bytes);
        ulong r1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8));
        ulong r2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16));
        ulong r3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24));
        ulong low = ZkBitOperations.Bswap64(r3);
        return (r0 | r1 | r2) == 0
            ? new UInt256(low, 0, 0, 0)
            : new UInt256(low,
                ZkBitOperations.Bswap64(r2),
                ZkBitOperations.Bswap64(r1),
                ZkBitOperations.Bswap64(r0));
    }
}
