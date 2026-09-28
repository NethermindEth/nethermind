// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <remarks>
    /// The guest has no 32-byte register, so the standard form's <see cref="EvmWord"/> local lives on the
    /// frame: the bottom word is copied out to it, the top copied over the bottom, and the saved copy read
    /// back over the top - 24 memory operations, and a frame slot that makes the handler use
    /// <c>ra</c> as a scratch register. Reading all eight limbs before writing any keeps the swap in
    /// registers: 16. The guest's stack slots are word aligned (see <c>StackPool.zkevm.cs</c>), as
    /// <see cref="SwapSlot"/> also relies on.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void SwapWords(ref byte bottom, ref byte top)
    {
        ref ulong low = ref Unsafe.As<byte, ulong>(ref bottom);
        ref ulong high = ref Unsafe.As<byte, ulong>(ref top);
        ulong low0 = low;
        ulong low1 = Unsafe.Add(ref low, 1);
        ulong low2 = Unsafe.Add(ref low, 2);
        ulong low3 = Unsafe.Add(ref low, 3);
        ulong high0 = high;
        ulong high1 = Unsafe.Add(ref high, 1);
        ulong high2 = Unsafe.Add(ref high, 2);
        ulong high3 = Unsafe.Add(ref high, 3);
        low = high0;
        Unsafe.Add(ref low, 1) = high1;
        Unsafe.Add(ref low, 2) = high2;
        Unsafe.Add(ref low, 3) = high3;
        high = low0;
        Unsafe.Add(ref high, 1) = low1;
        Unsafe.Add(ref high, 2) = low2;
        Unsafe.Add(ref high, 3) = low3;
    }
}
