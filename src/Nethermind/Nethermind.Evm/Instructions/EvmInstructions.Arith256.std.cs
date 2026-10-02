// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void AddMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
        => UInt256.AddMod(in a, in b, in m, out result);

    // The last four-limb MULMOD modulus this thread saw, and a reducer once it was seen twice in a row.
    [ThreadStatic] private static UInt256 t_lastMulModModulus;
    [ThreadStatic] private static MulModReducer? t_mulModReducer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void MulMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        if (Core.Diagnostics.ExperimentKnobs.MulModBarrett && m.u3 != 0)
        {
            MulModReducer? reducer = t_mulModReducer;
            if (reducer is not null && reducer.Reduces(in m))
            {
                reducer.MultiplyMod(in a, in b, out result);
                return;
            }

            if (MulModRepeats(in m))
            {
                t_mulModReducer = reducer = new MulModReducer(in m);
                reducer.MultiplyMod(in a, in b, out result);
                return;
            }
        }

        UInt256.MultiplyMod(in a, in b, in m, out result);
    }

    /// <summary>Whether the modulus is the one the previous four-limb MULMOD used; remembers it either way.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool MulModRepeats(in UInt256 m)
    {
        if (t_lastMulModModulus == m) return true;
        t_lastMulModModulus = m;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Divide256(in UInt256 a, in UInt256 b, out UInt256 result)
        => UInt256.Divide(in a, in b, out result);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Mod256(in UInt256 a, in UInt256 b, out UInt256 result)
        => UInt256.Mod(in a, in b, out result);
}
