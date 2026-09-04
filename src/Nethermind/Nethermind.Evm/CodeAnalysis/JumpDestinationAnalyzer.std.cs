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
        nuint length = (nuint)code.Length;
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        while (programCounter < length)
        {
            int op = Unsafe.AddByteOffset(ref codeRef, programCounter);

            // Everything outside [JUMPDEST, PUSH32] advances by one; this covers ~3/4 of real bytecode.
            if ((uint)(op - JUMPDEST) > PUSH32 - JUMPDEST)
            {
                programCounter++;
                continue;
            }

            if (op == JUMPDEST)
            {
                if ((programCounter ^ flagsPosition) >> BitShiftPerInt64 != 0 && currentFlags != 0)
                {
                    MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
                    currentFlags = 0;
                }

                // Shift wraps at 64, matching the bit's position within its segment.
                currentFlags |= 1L << (int)programCounter;
                flagsPosition = programCounter;
                programCounter++;
            }
            else if (op >= PUSH1)
            {
                // Fast forward past the push data; it holds no jump destinations.
                programCounter += (nuint)op - PUSH1 + 2;
            }
            else
            {
                // 0x5c-0x5f (TLOAD/TSTORE/MCOPY/PUSH0): no immediate data, single-byte advance.
                programCounter++;
            }
        }

        if (currentFlags != 0)
        {
            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
        }
    }
}
