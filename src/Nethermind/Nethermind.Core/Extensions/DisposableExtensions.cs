// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Core.Extensions;

public static class DisposableExtensions
{
    public static void TryDispose<T>(this T item)
    {
        if (item is IDisposable d)
        {
            d.Dispose();
        }
    }

    public static void DisposeItems<T>(this IEnumerable<T> item) where T : IDisposable
    {
        foreach (T disposable in item)
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
