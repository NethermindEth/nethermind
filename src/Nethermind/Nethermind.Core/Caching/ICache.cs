// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Caching
{
    public interface ICache<in TKey, TValue>
    {
        void Clear();
        TValue? Get(TKey key);
        bool TryGet(TKey key, out TValue? value);
        void Set(TKey key, TValue val);
        void Delete(TKey key);
        bool Contains(TKey key);
    }
}
