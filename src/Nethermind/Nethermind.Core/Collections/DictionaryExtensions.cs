// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Resettables;

namespace Nethermind.Core.Collections;

public static class DictionaryExtensions
{
    public static void Increment<TKey>(this Dictionary<TKey, int> dictionary, TKey key) where TKey : notnull
    {
        ref int res = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out bool _);
        res++;
    }

    extension<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
    {
        public ref TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, out bool exists)
        {
            ref TValue? existing = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out exists);

            if (!exists)
                existing = factory(key);

            return ref existing!;
        }

        public ref TValue GetOrAdd(TKey key, Func<TKey, TValue> factory) => ref dictionary.GetOrAdd(key, factory, out _);

    }

    /// <param name="dictionary">The dictionary whose values will be returned and cleared.</param>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary, which must implement <see cref="IReturnable"/>.</typeparam>
    extension<TKey, TValue>(IDictionary<TKey, TValue> dictionary) where TValue : class, IReturnable
    {
        /// <summary>
        /// Returns all values in the dictionary to their pool by calling <see cref="IReturnable.Return"/> on each value,
        /// then clears the dictionary.
        /// </summary>
        /// <remarks>
        /// Use this method when you need to both return pooled objects and clear the dictionary in one operation.
        /// </remarks>
        public void ResetAndClear()
        {
            foreach (TValue value in dictionary.Values)
            {
                value.Return();
            }
            dictionary.Clear();
        }
    }

    extension<TKey, TValue, TAlternateKey>(Dictionary<TKey, TValue>.AlternateLookup<TAlternateKey> dictionary)
        where TKey : notnull where TAlternateKey : notnull, allows ref struct
    {
        public bool TryRemove(TAlternateKey key, [MaybeNullWhen(false)] out TValue value) =>
            dictionary.Remove(key, out _, out value);
    }
}
