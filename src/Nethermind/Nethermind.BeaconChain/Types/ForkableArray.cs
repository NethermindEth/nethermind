// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Types;

/// <summary>Shares unexposed array snapshots across forks. A public array may be retained and written by its caller.</summary>
internal sealed class ForkableArray<T>(T[] items, bool exposed)
{
    internal T[] Items => items;

    internal ForkableArray<T> Fork() => exposed ? new((T[])items.Clone(), false) : this;

    internal static ForkableArray<T>? FromArray(T[]? value) => value is null ? null : new(value, true);

    internal static T[]? Expose(ref ForkableArray<T>? field)
    {
        while (Volatile.Read(ref field) is { } current)
        {
            if (current.IsExposed) return current.Items;
            ForkableArray<T> writable = new((T[])current.Items.Clone(), true);
            if (Interlocked.CompareExchange(ref field, writable, current) == current) return writable.Items;
        }
        return null;
    }

    private bool IsExposed => exposed;
}
