// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>Empties a <see cref="List{T}"/> without zeroing its backing array.</summary>
/// <remarks>
/// <see cref="List{T}.Clear"/> zeroes the used slots of a list whose items hold references, so the garbage collector
/// can reclaim what they point to. The guest validates a single block, so a stale slot only keeps its target alive
/// until the list refills it, and the per-transaction journals would otherwise pay for the zeroing on every transaction.
/// </remarks>
internal static class GuestList<T>
{
    public static void Reset(List<T> list)
    {
        Size(list) = 0;
        Version(list)++;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
    private static extern ref int Size(List<T> list);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    private static extern ref int Version(List<T> list);
}
