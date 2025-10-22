// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

public static class DictionaryExtensions
{
    public static void Increment<TKey>(this Dictionary<TKey, int> dictionary, TKey key) where TKey : notnull
    {
        ref int res = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out bool _);
        res++;
    }

    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary,
        TKey key, Func<TKey, TValue> factory,
        out bool exists)
        where TKey : notnull
    {
        ref TValue? existing = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out exists);

        if (!exists)
            existing = factory(key);

        return existing!;
    }

    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary,
        TKey key, Func<TKey, TValue> factory)
        where TKey : notnull =>
        GetOrAdd(dictionary, key, factory, out _);
}
