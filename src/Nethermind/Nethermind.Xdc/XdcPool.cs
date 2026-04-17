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
}

public interface IXdcPoolItem
{
    (ulong Round, Hash256 hash) PoolKey();
    Address? Signer { get; }
}
