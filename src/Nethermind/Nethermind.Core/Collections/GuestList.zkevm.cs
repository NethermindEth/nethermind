// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>Empties or truncates a <see cref="List{T}"/> without zeroing its backing array.</summary>
/// <remarks>
/// <see cref="List{T}.Clear"/> and <see cref="System.Runtime.InteropServices.CollectionsMarshal.SetCount{T}"/> zero the
/// dropped slots of a list whose items hold references, so the garbage collector can reclaim what they point to. The
/// guest validates a single block, so a stale slot only keeps its target alive until the list refills it, and the
/// per-transaction journals would otherwise pay for the zeroing on every transaction and restore.
/// </remarks>
internal static class GuestList<T>
{
    public static void Reset(List<T> list) => Truncate(list, 0);

    /// <param name="count">The new count, no greater than the current one.</param>
    public static void Truncate(List<T> list, int count)
    {
        Size(list) = count;
        Version(list)++;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
    private static extern ref int Size(List<T> list);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    private static extern ref int Version(List<T> list);
}
