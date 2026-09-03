// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <summary>Writes <paramref name="value"/> as one big-endian 32-byte stack word.</summary>
    /// <remarks>
    /// Carries the same small-value shortcut as <see cref="ReadBeWord"/>: pushed values are
    /// dominated by counters and offsets whose high limbs are zero, which need one lane swapped
    /// instead of four. See <c>EvmStack.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBeWord(ref EvmWord head, in UInt256 value)
    {
        ref ulong d = ref Unsafe.As<EvmWord, ulong>(ref head);
        if ((value.u1 | value.u2 | value.u3) == 0)
        {
            d = 0;
            Unsafe.Add(ref d, 1) = 0;
            Unsafe.Add(ref d, 2) = 0;
            Unsafe.Add(ref d, 3) = ZkEvmBitOperations.Bswap64(value.u0);
        }
        else
        {
            ZkEvmBitOperations.Bswap256(in value, ref head);
        }
    }

    /// <summary>Reads one big-endian 32-byte stack word into a <see cref="UInt256"/>.</summary>
    /// <remarks>
    /// RISC-V has no byte-swap instruction, so reversing endianness is a software shuffle. Words
    /// produced by PUSH0/PUSH1/PUSH2 and the like have their high 24 bytes zero, so the common case
    /// swaps only the low limb instead of all four. See <c>EvmStack.std.cs</c> for the host form.
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt256 ReadBeWord(ref byte bytes)
    {
        ulong r0 = Unsafe.ReadUnaligned<ulong>(ref bytes);
        ulong r1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8));
        ulong r2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16));
        ulong r3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24));
        ulong low = ZkEvmBitOperations.Bswap64(r3);
        return (r0 | r1 | r2) == 0
            ? new UInt256(low, 0, 0, 0)
            : new UInt256(
                low,
                ZkEvmBitOperations.Bswap64(r2),
                ZkEvmBitOperations.Bswap64(r1),
                ZkEvmBitOperations.Bswap64(r0)
            );
    }

    /// <inheritdoc cref="ReadBeWords(ref byte, out UInt256, out UInt256, out UInt256, out UInt256)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadBeWords(ref byte bytes, out UInt256 a, out UInt256 b)
    {
        b = ReadBeWord(ref bytes);
        a = ReadBeWord(ref Unsafe.Add(ref bytes, 32));
    }

    /// <inheritdoc cref="ReadBeWords(ref byte, out UInt256, out UInt256, out UInt256, out UInt256)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadBeWords(ref byte bytes, out UInt256 a, out UInt256 b, out UInt256 c)
    {
        c = ReadBeWord(ref bytes);
        b = ReadBeWord(ref Unsafe.Add(ref bytes, 32));
        a = ReadBeWord(ref Unsafe.Add(ref bytes, 64));
    }

    /// <summary>Reads adjacent big-endian stack words, <paramref name="a"/> being the top of the stack.</summary>
    /// <remarks>
    /// One <see cref="ReadBeWord"/> per word: the guest has no out-of-order execution, so the
    /// interleaving the host variant relies on buys nothing. The words sit deepest first, so
    /// <paramref name="bytes"/> addresses the last parameter and the top of the stack is the
    /// highest offset.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadBeWords(ref byte bytes, out UInt256 a, out UInt256 b, out UInt256 c, out UInt256 d)
    {
        d = ReadBeWord(ref bytes);
        c = ReadBeWord(ref Unsafe.Add(ref bytes, 32));
        b = ReadBeWord(ref Unsafe.Add(ref bytes, 64));
        a = ReadBeWord(ref Unsafe.Add(ref bytes, 96));
    }
}
