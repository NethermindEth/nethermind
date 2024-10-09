// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Core.Collections;

public static class EnumerableExtensions
{
    public static void ForEach<T>(this IEnumerable<T> list, Action<T> action)
    {
        foreach (T element in list)
        {
            action(element);
        }
    }

    public static bool NullableSequenceEqual<T>(this IEnumerable<T>? first, IEnumerable<T>? second) =>
        first is not null ? second is not null && first.SequenceEqual(second) : second is null;

    public static bool ContainsDuplicates<T>(this IEnumerable<T> list, int? count = null, IEqualityComparer<T>? comparer = null)
    {
        HashSet<T> hashSet = count is null ? new HashSet<T>(comparer) : new HashSet<T>(count.Value, comparer);
        foreach (T element in list)
        {
            if (!hashSet.Add(element))
            {
                return true;
            }
        }

        return false;
    }
}
