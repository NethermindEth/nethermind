// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Test;

internal static class PbtIteratorTestExtensions
{
    internal static List<T> Drain<T>(this IPbtIterator<T> iterator)
    {
        using (iterator)
        {
            List<T> items = [];
            while (iterator.MoveNext()) items.Add(iterator.Current);
            return items;
        }
    }
}
