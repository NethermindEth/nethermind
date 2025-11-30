// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Resettables;

namespace Nethermind.Core.Extensions;

public static class DictionaryExtensions
{
    /// <summary>
    /// Returns all values in the dictionary to their pool by calling <see cref="IReturnable.Return"/> on each value,
    /// then clears the dictionary.
    /// </summary>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary, which must implement <see cref="IReturnable"/>.</typeparam>
    /// <param name="dictionary">The dictionary whose values will be returned and cleared.</param>
    /// <remarks>
    /// Use this method when you need to both return pooled objects and clear the dictionary in one operation.
    /// </remarks>
    public static void ResetAndClear<TKey, TValue>(this IDictionary<TKey, TValue> dictionary)
        where TValue : class, IReturnable
    {
        foreach (TValue value in dictionary.Values)
        {
            value.Return();
        }
        dictionary.Clear();
    }
}
