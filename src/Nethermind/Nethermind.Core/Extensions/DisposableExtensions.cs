// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Nethermind.Core.Extensions;

public static class DisposableExtensions
{
    /// <summary>
    /// Attempts to dispose <paramref name="item"/> if it implements <see cref="IDisposable"/>.
    /// For <see cref="ITuple"/> values (e.g. value tuples) that don't implement IDisposable,
    /// each element is individually checked and disposed.
    /// Accepts <c>object?</c> so the constrained overloads are preferred by the compiler.
    /// </summary>
    public static void TryDispose(this object? item)
    {
        if (item is IDisposable d)
        {
            d.Dispose();
        }
        else if (item is ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                if (tuple[i] is IDisposable element)
                    element.Dispose();
            }
        }
    }

    /// <summary>
    /// Constrained overload for known-disposable types. Avoids boxing and the ITuple check.
    /// </summary>
    public static void TryDispose<T>(this T item) where T : IDisposable => item?.Dispose();

    /// <summary>
    /// Overload for 2-element value tuples where the first element is <see cref="IDisposable"/>.
    /// Avoids boxing the tuple into <see cref="ITuple"/>.
    /// </summary>
    public static void TryDispose<T1, T2>(this in (T1, T2) item) where T1 : IDisposable
    {
        item.Item1?.Dispose();
        if (item.Item2 is IDisposable d2) d2.Dispose();
    }

    public static void DisposeItems<T>(this IEnumerable<T> items) where T : IDisposable
    {
        foreach (T disposable in items)
        {
            disposable.Dispose();
        }
    }

    public static async ValueTask DisposeItemsAsync<T>(this IEnumerable<T> item) where T : IAsyncDisposable
    {
        foreach (T disposable in item)
        {
            await disposable.DisposeAsync();
        }
    }


    public static ValueTask TryDisposeAsync<T>(this T item)
    {
        switch (item)
        {
            case IAsyncDisposable d1:
                return d1.DisposeAsync();
            case IDisposable d2:
                d2.Dispose();
                break;
        }

        return default;
    }
}
