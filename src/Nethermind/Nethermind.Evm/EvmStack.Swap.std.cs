// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void SwapWords(ref byte bottom, ref byte top)
    {
        EvmWord buffer = Unsafe.ReadUnaligned<EvmWord>(ref bottom);
        Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<EvmWord>(ref top));
        Unsafe.WriteUnaligned(ref top, buffer);
    }
}
