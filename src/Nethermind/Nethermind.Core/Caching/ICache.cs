// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Caching
{
    public interface ICache<in TKey, TValue>
    {
        void Clear();
        TValue? Get(TKey key);
        bool TryGet(TKey key, out TValue? value);

        /// <summary>
        /// Sets value in the cache.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="val"></param>
        /// <returns>True if key didn't exist in the cache, otherwise false.</returns>
        bool Set(TKey key, TValue val);

        /// <summary>
        /// Delete key from cache.
        /// </summary>
        /// <param name="key"></param>
        /// <returns>True if key existed in the cache, otherwise false.</returns>
        bool Delete(TKey key);
        bool Contains(TKey key);
    }
}
