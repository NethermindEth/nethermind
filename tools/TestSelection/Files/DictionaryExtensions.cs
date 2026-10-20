// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TestSelection.Files;

internal static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key) where TKey : notnull where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out TValue? value))
        {
            dictionary[key] = value = new TValue();
        }

        return value;
    }
}
