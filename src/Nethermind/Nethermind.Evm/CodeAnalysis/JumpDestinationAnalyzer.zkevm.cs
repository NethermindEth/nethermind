// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class JumpDestinationAnalyzer
{
    [SkipLocalsInit]
    private static partial void ProcessJumpDestinationBitmap_Scalar(nuint programCounter, Span<long> bitmap, ReadOnlySpan<byte> code)
    {
        // Flags for the 64-bit bitmap segment holding the last JUMPDEST seen; flushed only when a
        // later JUMPDEST lands in a different segment (and once at the end), so the common bytes -
        // neither JUMPDEST nor PUSH - pay a single unsigned range check and nothing else.
        long currentFlags = 0;
        nuint flagsPosition = 0;
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        // Walked as a moving reference rather than a base plus index: the index form recomputes the
        // address every byte, and only a JUMPDEST needs the position back.
        ref byte position = ref Unsafe.AddByteOffset(ref codeRef, programCounter);
        ref byte end = ref Unsafe.AddByteOffset(ref codeRef, (nuint)code.Length);
        while (Unsafe.IsAddressLessThan(in position, in end))
        {
            // Biased by JUMPDEST: one unsigned compare rejects everything outside [JUMPDEST, PUSH32]
            // (~3/4 of real bytecode), the JUMPDEST test below is then a compare against zero, which
            // needs no constant register, and the PUSH advance reuses the same difference. Kept 32-bit
            // - widening the byte to a pointer costs a zero-extension pair on every byte scanned.
            int op = position - JUMPDEST;

            if ((uint)op > PUSH32 - JUMPDEST)
            {
                position = ref Unsafe.Add(ref position, 1);
                continue;
            }

            if (op == 0)
            {
                nuint jumpDestination = (nuint)Unsafe.ByteOffset(ref codeRef, ref position);
                if ((jumpDestination ^ flagsPosition) >> BitShiftPerInt64 != 0 && currentFlags != 0)
                {
                    MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
                    currentFlags = 0;
                }

                // Shift wraps at 64, matching the bit's position within its segment.
                currentFlags |= 1L << (int)jumpDestination;
                flagsPosition = jumpDestination;
                position = ref Unsafe.Add(ref position, 1);
            }
            else if (op >= PUSH1 - JUMPDEST)
            {
                // Fast forward past the push data; it holds no jump destinations.
                position = ref Unsafe.Add(ref position, op - (PUSH1 - JUMPDEST) + 2);
            }
            else
            {
                // 0x5c-0x5f (TLOAD/TSTORE/MCOPY/PUSH0): no immediate data, single-byte advance.
                position = ref Unsafe.Add(ref position, 1);
            }
        }

        if (currentFlags != 0)
        {
            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
        }
    }
}
