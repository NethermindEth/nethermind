// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public static ValueTask TryDisposeAsync<T>(this T item) =>
        item is IAsyncDisposable d ? d.DisposeAsync() : default;
}
