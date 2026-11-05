// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

internal static unsafe partial class NativeMemoryListCore<T> where T : unmanaged
{
    private static partial bool UseAlignedAlloc
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitOperations.IsPow2(sizeof(T));
    }
}
