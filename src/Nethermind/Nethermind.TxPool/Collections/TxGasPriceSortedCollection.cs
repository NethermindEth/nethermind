// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.TxPool.Collections;

public partial class TxGasPriceSortedCollection
{
    private static TxGasPriceSortedCollection? _instance;

    public static TxGasPriceSortedCollection Instance()
    {
        _instance ??= new TxGasPriceSortedCollection();
        return _instance;
    }
    private McsPriorityLock Lock { get; } = new();
    private readonly int _capacity;
    private readonly EnhancedSortedSet<TxHashGasPricePair> _sortedSet;
    private readonly Dictionary<ValueHash256, UInt256> _cacheMap;
    private bool _isFull = false;

    public struct TxHashGasPricePair
    {
        public ValueHash256 Hash { get; }
        public UInt256 GasPrice { get; }

        public TxHashGasPricePair(ValueHash256 hash, UInt256 gasPrice)
        {
            Hash = hash;
            GasPrice = gasPrice;
        }
    }

    public class GasPriceComparer : IComparer<TxHashGasPricePair>
    {
        public int Compare(TxHashGasPricePair x, TxHashGasPricePair y)
        {
            if (x.GasPrice == y.GasPrice)
            {
                return x.Hash.CompareTo(y.Hash);
            }
            return y.GasPrice.CompareTo(x.GasPrice);
        }
    }

    public TxGasPriceSortedCollection(int capacity = 16)
    {
        _capacity = capacity;
        _cacheMap = [];
        _sortedSet = new EnhancedSortedSet<TxHashGasPricePair>(new GasPriceComparer());
    }

    public int Count => _cacheMap.Count;

    public bool TryRemove(Transaction tx, [NotNullWhen(true)] out TxHashGasPricePair? removed)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();

        if (tx is null)
        {
            removed = default;
            return false;
        }

        ValueHash256 hash = tx.Hash;
        return TryRemoveNonLocked(hash, false, out removed);
    }

    private bool TryRemoveNonLocked(ValueHash256 hash, bool evicted, [NotNullWhen(true)] out TxHashGasPricePair? removed)
    {
        if (_cacheMap.TryGetValue(hash, out UInt256 gasPrice))
        {
            TxHashGasPricePair pair = new TxHashGasPricePair(hash, gasPrice);

            if (_cacheMap.Remove(hash))
            {
                UpdateIsFull();
                TxHashGasPricePair hashGasPricePair = new TxHashGasPricePair(hash, gasPrice);

                if (_sortedSet.Remove(hashGasPricePair))
                {
                    removed = hashGasPricePair;
                    return true;
                }

                Removed?.Invoke(this, new TxGasPriceSortedCollectionRemovedEventArgs(hash, gasPrice, evicted));
            }
        }

        removed = default;
        return false;
    }

    public bool TryRemove(ValueHash256 hash)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();
        return TryRemoveNonLocked(hash, false, out _);
    }

    public bool TryRemove(Transaction tx) => TryRemove(tx, out _);

    public bool TryInsert(ref Transaction tx, out TxHashGasPricePair? removed)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();

        if (tx is null)
        {
            removed = default;
            return false;
        }

        ValueHash256 hash = tx.CalculateHash();
        UInt256 gasPrice = tx.GasPrice;

        if (CanInsert(hash, gasPrice))
        {
            if (_sortedSet.Add(new TxHashGasPricePair(hash, gasPrice)))
            {
                _cacheMap[hash] = gasPrice;
                UpdateIsFull();
                Inserted?.Invoke(this, new TxGasPriceSortedCollectionEventArgs(hash, gasPrice));

                if (_cacheMap.Count > _capacity)
                {
                    if (!RemoveLast(out removed) || _cacheMap.Count > _capacity)
                    {
                        RemoveLast(out removed);
                    }

                    return true;
                }

                removed = default;
                return false;
            }
        }

        removed = default;
        return false;
    }

    public bool TryInsert(ref Transaction tx) => TryInsert(ref tx, out _);

    private bool CanInsert(ValueHash256 hash, UInt256? gasPrice)
    {
        if (gasPrice is null)
        {
            throw new ArgumentNullException();
        }

        return !_cacheMap.ContainsKey(hash);
    }

    public bool RemoveLast(out TxHashGasPricePair? removed)
    {

        TxHashGasPricePair toRemove = _sortedSet.Max;
        return TryRemoveNonLocked(toRemove.Hash, true, out removed);
    }

    public bool TryGetTxHashGasPricePair(ValueHash256 hash, [NotNullWhen(true)] out TxHashGasPricePair pair)
    {
        using McsLock.Disposable lockRelease = Lock.Acquire();

        if (_cacheMap.TryGetValue(hash, out UInt256 gasPrice))
        {
            pair = new TxHashGasPricePair(hash, gasPrice);
            return true;
        }

        pair = default;
        return false;
    }

    public bool IsFull() => _isFull;

    public TxHashGasPricePair GetFirstPair() => _sortedSet.Min;

    private void UpdateIsFull() => _isFull = _cacheMap.Count >= _capacity;

    private static string CollectionName => "TxGasPriceSortedCollection";
}
