// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Caching
{
    /// <summary>
    /// Its like `ICache` but you can index the key by span
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    public interface ISpanCache<TKey, TValue>
    {
        void Clear();
        TValue? Get(ReadOnlySpan<TKey> key);
        bool TryGet(ReadOnlySpan<TKey> key, out TValue? value);

        /// <summary>
        /// Sets value in the cache.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="val"></param>
        /// <returns>True if key didn't exist in the cache, otherwise false.</returns>
        bool Set(ReadOnlySpan<TKey> key, TValue val);

        /// <summary>
        /// Delete key from cache.
        /// </summary>
        /// <param name="key"></param>
        /// <returns>True if key existed in the cache, otherwise false.</returns>
        bool Delete(ReadOnlySpan<TKey> key);
        bool Contains(ReadOnlySpan<TKey> key);
    }
}
