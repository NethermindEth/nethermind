// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

public static class DictionaryExtensions
{
    /// <summary>
    /// Mirrors <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>
    /// so that <see cref="Dictionary{TKey,TValue}"/> can be used with the same API.
    /// </summary>
    public static bool TryRemove<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, [MaybeNullWhen(false)] out TValue value)
        where TKey : notnull
        => dictionary.Remove(key, out value);

    public static void Increment<TKey>(this Dictionary<TKey, int> dictionary, TKey key) where TKey : notnull
    {
        ref int res = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out bool _);
        res++;
    }

    extension<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
    {
        public ref TValue GetOrAdd(TKey key, Func<TKey, TValue> factory,
            out bool exists)
        {
            ref TValue? existing = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out exists);

            if (!exists)
                existing = factory(key);

            return ref existing!;
        }

        public ref TValue GetOrAdd(TKey key, Func<TKey, TValue> factory) => ref dictionary.GetOrAdd(key, factory, out _);

        public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        {
            ref TValue value = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary, key);
            if (!Unsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, comparisonValue))
            {
                value = newValue;
                return true;
            }
            return false;
        }

        public bool NoResizeClear()
        {
            dictionary.Clear();
            return true;
        }
    }
}
