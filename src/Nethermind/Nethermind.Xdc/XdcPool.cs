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
    private readonly Dictionary<(ulong Round, Hash256 Hash), Dictionary<Address, T>> _items = [];
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

    internal void RemoveRoundsOutsideRetention(ulong latestRound, ulong retainedRoundCount)
    {
        if (latestRound < retainedRoundCount)
            return;

        EndRound(latestRound - retainedRoundCount);
    }

    public IReadOnlyCollection<T> GetItemsByKey(T item)
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

    public IReadOnlyList<IReadOnlyCollection<T>> GetGroupsByRound(ulong round)
    {
        using McsLock.Disposable lockRelease = _lock.Acquire();
        List<IReadOnlyCollection<T>> result = [];
        foreach (KeyValuePair<(ulong Round, Hash256 Hash), Dictionary<Address, T>> pair in _items)
        {
            if (pair.Key.Round == round)
                result.Add(pair.Value.Values.ToArray());
        }
        return result;
    }

    public IDictionary<(ulong Round, Hash256 Hash), Dictionary<Address, T>> GetItems()
    {
        using McsLock.Disposable lockRelease = _lock.Acquire();
        {
            Dictionary<(ulong Round, Hash256 Hash), Dictionary<Address, T>> snapshot = new(_items.Count);
            foreach (KeyValuePair<(ulong Round, Hash256 Hash), Dictionary<Address, T>> pair in _items)
            {
                snapshot[pair.Key] = new Dictionary<Address, T>(pair.Value);
            }

            return snapshot;
        }
    }

    // Forensics needs same-round votes across different pool keys to detect signer equivocation.
    public IReadOnlyCollection<T> GetItemsFromRoundExcludingKey(T item)
    {
        using McsLock.Disposable lockRelease = _lock.Acquire();
        {
            (ulong Round, Hash256 hash) targetKey = item.PoolKey();
            List<T> items = [];
            foreach (KeyValuePair<(ulong Round, Hash256 Hash), Dictionary<Address, T>> pair in _items)
            {
                (ulong Round, Hash256 Hash) key = pair.Key;
                if (key.Round != targetKey.Round || key.Hash == targetKey.hash)
                {
                    continue;
                }

                items.AddRange(pair.Value.Values);
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
