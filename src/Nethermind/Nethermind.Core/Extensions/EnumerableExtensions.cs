// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Core.Extensions
{
    public static class EnumerableExtensions
    {
        public static ISet<T> AsSet<T>(this IEnumerable<T> enumerable) =>
            enumerable is ISet<T> set ? set : enumerable.ToHashSet();
    }
}
