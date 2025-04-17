// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public static class DictionaryExtensions
{
    public static void Increment<TKey>(this IDictionary<TKey, int> dictionary, TKey key)
    {
        if (!dictionary.TryAdd(key, 1)) dictionary[key]++;
    }
}
