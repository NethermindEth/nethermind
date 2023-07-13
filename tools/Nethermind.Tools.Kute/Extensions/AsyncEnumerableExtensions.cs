// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Extensions;

public static class AsyncEnumerableExtensions
{
    public static IAsyncEnumerable<(TSource, int)> Indexed<TSource>(this IAsyncEnumerable<TSource> source, int startingFrom = 0)
    {
        return source.Select((t, idx) => (t, startingFrom + idx));
    }
}
