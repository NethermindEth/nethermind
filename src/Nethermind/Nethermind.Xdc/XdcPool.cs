// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;

namespace Nethermind.Xdc;

public class XdcPool<T> where T: IXdcPoolItem
{
    private readonly Dictionary<(ulong Round, Hash256 Hash), ArrayPoolList<T>> _items = new();
    private readonly McsLock _lock = new();

    public long Add(T item)
    {
        using var lockRelease = _lock.Acquire();
        {
            var key = item.PoolKey();
            if (!_items.TryGetValue(key, out var list))
            {
                //128 should be enough to cover all master nodes and some extras
                list = new ArrayPoolList<T>(128);
                _items[key] = list;
            }
            if (!list.Contains(item))
                list.Add(item);
            return list.Count;
        }
    }

    public void EndRound(ulong round)
    {
        using var lockRelease = _lock.Acquire();
        {
            foreach (var key in _items.Keys.ToArray())
            {
                if (key.Round <= round && _items.Remove(key, out ArrayPoolList<T> list))
                {
                    list?.Dispose();
                }
            }
        }
    }

    public IReadOnlyCollection<T> GetItems(T item)
    {
        using var lockRelease = _lock.Acquire();
        {
            var key = item.PoolKey();
            if (_items.TryGetValue(key, out ArrayPoolList<T> list))
            {
                //Allocating a new array since it goes outside the lock
                return list.ToArray();
            }
            return [];
        }
    }

    public long GetCount(T item)
    {
        using var lockRelease = _lock.Acquire();
        {
            var key = item.PoolKey();
            if (_items.TryGetValue(key, out ArrayPoolList<T> list))
            {
                return list.Count;
            }
            return 0;
        }
    }
}

public interface IXdcPoolItem
{
    (ulong Round, Hash256 hash) PoolKey();
}
