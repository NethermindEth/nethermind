// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>Bench-only: a hash of the conditional jumps a thread took, in order.</summary>
public static class PathProbe
{
    [ThreadStatic] public static ulong Hash;
    [ThreadStatic] public static bool Armed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mix(uint destination, bool taken) =>
        Hash = ((Hash ^ (((ulong)destination << 1) | (taken ? 1UL : 0UL))) * 0x100000001B3UL) + 0x9E3779B97F4A7C15UL;
}
