// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class JumpDestinationAnalyzer
{
    /// <summary>The scan's two comparands, in the order it reads them: <c>JUMPDEST</c> then <c>PUSH1</c>.</summary>
    /// <remarks>
    /// ILC re-materialises a compared-against constant at every use inside a loop, and the preinitialiser
    /// folds <c>static readonly</c> scalars straight back into that. An array element is opaque to it, so
    /// reading these once before the scan keeps them in registers.
    /// </remarks>
    private static readonly int[] _byteScanThresholds = [JUMPDEST, PUSH1];

    /// <remarks>
    /// Zisk reaches every byte in four instructions, so the word scanner the std build keeps cannot
    /// win: it processes a word per PUSH rather than skipping one, and all real bytecode is PUSH-dense.
    /// </remarks>
    [SkipLocalsInit]
    internal static long[] PopulateJumpDestinationBitmap_Scalar(long[] bitmap, ReadOnlySpan<byte> code)
    {
        ProcessJumpDestinationBitmap_Byte(programCounter: 0, bitmap, code);

        return bitmap;
    }

    /// <remarks>
    /// Walks a moving pointer instead of a base plus an index: ILC recomputes the byte address on
    /// every step of the indexed form, and the position is only needed at the rare JUMPDEST. The
    /// indexed form is the faster one on x64, hence the split.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe void ProcessJumpDestinationBitmap_Byte(nuint programCounter, Span<long> bitmap, ReadOnlySpan<byte> code)
    {
        long currentFlags = 0;
        nuint flagsPosition = 0;
        ref int thresholds = ref MemoryMarshal.GetArrayDataReference(_byteScanThresholds);
        int jumpDest = thresholds;
        int push1 = Unsafe.Add(ref thresholds, 1);
        // The PUSH skip below steps up to 32 bytes past the end of the code, so the walk pins and moves
        // an unmanaged pointer: the same overshoot on a `ref byte` is a managed pointer outside its
        // object, which a relocating GC may adjust wrongly even though it is only ever compared - and
        // the differential tests run this scan on CoreCLR, whose GC does relocate.
        fixed (byte* codeStart = code)
        {
            byte* position = codeStart + programCounter;
            byte* end = codeStart + code.Length;
            while (position < end)
            {
                // Sign extension folds everything above PUSH32 below JUMPDEST, so one signed comparison
                // covers the whole [JUMPDEST, PUSH32] window that the rebase-and-range-test needed two for.
                int op = (sbyte)*position;
                if (op >= jumpDest)
                {
                    if (op >= push1)
                    {
                        // One byte short: every path joins the single advance below, so nothing branches over it.
                        position += op - PUSH1 + 1;
                    }
                    else if (op == jumpDest)
                    {
                        nuint jumpDestination = (nuint)(position - codeStart);
                        if ((jumpDestination ^ flagsPosition) >> BitShiftPerInt64 != 0 && currentFlags != 0)
                        {
                            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
                            currentFlags = 0;
                        }

                        currentFlags |= 1L << (int)jumpDestination;
                        flagsPosition = jumpDestination;
                    }
                }

                position++;
            }
        }

        if (currentFlags != 0)
        {
            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
        }
    }
}
