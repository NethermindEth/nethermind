// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc;

public class XdcPool<T> where T : IXdcPoolItem
{
    private readonly Dictionary<(ulong Round, Hash256 Hash), Dictionary<Address, T>> _items = new();
    private readonly McsLock _lock = new();

    public long Add(T item)
    {
        if (item.Signer is null)
            throw new ArgumentException($"Signature must be recovered before adding {typeof(T).Name}.");

        using McsLock.Disposable lockRelease = _lock.Acquire();
        {
            (ulong Round, Hash256 hash) key = item.PoolKey();
            if (!_items.TryGetValue(key, out Dictionary<Address, T> list))
            {
                //128 should be enough to cover all master nodes and some extras
                list = new Dictionary<Address, T>(128);
                _items[key] = list;
            }
            list.TryAdd(item.Signer, item);
            return list.Count;
        }
    }

    public void EndRound(ulong round)
    {
        using McsLock.Disposable lockRelease = _lock.Acquire();
        {
            foreach ((ulong Round, Hash256 Hash) key in _items.Keys)
            {
                if (key.Round <= round)
                {
                    _items.Remove(key, out Dictionary<Address, T> _);
                }
            }
        }
    }

    public IReadOnlyCollection<T> GetItems(T item)
    {
        using McsLock.Disposable lockRelease = _lock.Acquire();
        {
            (ulong Round, Hash256 hash) key = item.PoolKey();
            if (_items.TryGetValue(key, out Dictionary<Address, T> list))
            {
                //Allocating a new array since it goes outside the lock
                return list.Values.ToArray();
            }
            return [];
        }
    }

    public long GetCount(T item)
    {
        using McsLock.Disposable lockRelease = _lock.Acquire();
        {
            (ulong Round, Hash256 hash) key = item.PoolKey();
            if (_items.TryGetValue(key, out Dictionary<Address, T> list))
            {
                return list.Values.Count;
            }
            return 0;
        }
    }

    // Forensics needs same-round votes across different pool keys to detect signer equivocation.
    public IReadOnlyCollection<T> GetItemsFromRoundExcludingKey(T item)
    {
        using var lockRelease = _lock.Acquire();
        {
            (ulong Round, Hash256 hash) targetKey = item.PoolKey();
            List<T> items = [];
            foreach (KeyValuePair<(ulong Round, Hash256 Hash), ArrayPoolList<T>> pair in _items)
            {
                (ulong Round, Hash256 Hash) key = pair.Key;
                if (key.Round != targetKey.Round || key.Hash == targetKey.hash)
                {
                    continue;
                }

                ArrayPoolList<T> list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    items.Add(list[i]);
                }
            }

            return items;
        }
    }
}

public interface IXdcPoolItem
{
    (ulong Round, Hash256 hash) PoolKey();
    Address? Signer { get; }
}
