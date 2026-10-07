// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <remarks>
    /// The guest has no 32-byte register, so the standard form's <see cref="EvmWord"/> local lives on the
    /// frame and costs 24 memory operations. Reading all eight limbs before writing any (the previous form)
    /// needs eight live temporaries: the interpreter's arguments occupy a0-a7, so four of them landed in
    /// s1-s4 and every SWAP paid a prolog and epilog that saved and restored them (10 of its 42 steps).
    /// The two words never overlap (depth >= 1), so the limbs are exchanged one at a time: two loads, two
    /// stores, two temporaries, 16 memory operations and no callee-saved register. The guest's stack
    /// slots are word aligned (see <c>StackPool.zkevm.cs</c>), as <see cref="SwapSlot"/> also relies on.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void SwapWords(ref byte bottom, ref byte top)
    {
        ref ulong low = ref Unsafe.As<byte, ulong>(ref bottom);
        ref ulong high = ref Unsafe.As<byte, ulong>(ref top);

        ulong l = low;
        ulong h = high;
        low = h;
        high = l;

        l = Unsafe.Add(ref low, 1);
        h = Unsafe.Add(ref high, 1);
        Unsafe.Add(ref low, 1) = h;
        Unsafe.Add(ref high, 1) = l;

        l = Unsafe.Add(ref low, 2);
        h = Unsafe.Add(ref high, 2);
        Unsafe.Add(ref low, 2) = h;
        Unsafe.Add(ref high, 2) = l;

        l = Unsafe.Add(ref low, 3);
        h = Unsafe.Add(ref high, 3);
        Unsafe.Add(ref low, 3) = h;
        Unsafe.Add(ref high, 3) = l;
    }
}
