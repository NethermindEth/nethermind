// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void AddMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
        => UInt256.AddMod(in a, in b, in m, out result);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void MulMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
        => UInt256.MultiplyMod(in a, in b, in m, out result);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Divide256(in UInt256 a, in UInt256 b, out UInt256 result)
        => UInt256.Divide(in a, in b, out result);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Mod256(in UInt256 a, in UInt256 b, out UInt256 result)
        => UInt256.Mod(in a, in b, out result);
}
