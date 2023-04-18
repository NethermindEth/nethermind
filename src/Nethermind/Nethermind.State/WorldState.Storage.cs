// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public partial class WorldState
{
    private readonly ResettableDictionary<StorageCell, StackList<int>> _storageIntraBlockCache = new();

    private const int StorageStartCapacity = Resettable.StartCapacity;
    private int _storageCapacity = StorageStartCapacity;

    private StorageChange?[] _storageChanges = new StorageChange[StorageStartCapacity];
    private int _currentStoragePosition = Resettable.EmptyPosition;

    // stack of snapshot indexes on changes for start of each transaction
    // this is needed for OriginalValues for new transactions
    private readonly Stack<int> _transactionChangesSnapshots = new();

    private static readonly byte[] _zeroValue = { 0 };


    private readonly ResettableDictionary<Address, StorageTree> _storages = new();
    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new();
    private readonly ResettableHashSet<StorageCell> _storageCommittedThisRound = new();

    private Keccak RecalculateStorageRoot(Address address)
    {
        StorageTree storageTree = GetOrCreateStorage(address);
        storageTree.UpdateRootHash();
        return storageTree.RootHash;
    }

    private StorageTree GetOrCreateStorage(Address address)
    {
        if (_storages.TryGetValue(address, out StorageTree storageTree)) return storageTree;
        storageTree = new StorageTree(_trieStore, GetStorageRoot(address), _logManager);
        return _storages[address] = storageTree;
    }


    public byte[] Get(in StorageCell storageCell)
    {
        return GetCurrentValue(storageCell);
    }

    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        Debug.Assert(!storageCell.IsTransient);
        PushUpdate(storageCell, newValue);
    }

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    public byte[] GetOriginal(in StorageCell storageCell)
    {
        Debug.Assert(!storageCell.IsTransient);
        if (!_originalValues.ContainsKey(storageCell))
        {
            throw new InvalidOperationException("Get original should only be called after get within the same caching round");
        }

        if (_transactionChangesSnapshots.TryPeek(out int snapshot))
        {
            if (_storageIntraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
            {
                if (stack.TryGetSearchedItem(snapshot, out int lastChangeIndexBeforeOriginalSnapshot))
                {
                    return _storageChanges[lastChangeIndexBeforeOriginalSnapshot]!.Value;
                }
            }
        }

        return _originalValues[storageCell];
    }


    public byte[] GetTransientState(in StorageCell storageCell)
    {
        Debug.Assert(storageCell.IsTransient);
        return GetCurrentValue(storageCell);
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        Debug.Assert(storageCell.IsTransient);
        PushUpdate(storageCell, newValue);
    }

    private byte[] GetCurrentValue(in StorageCell storageCell)
    {
        if(TryGetCachedStorageValue(storageCell, out byte[] bytes))
        {
            return bytes!;
        }

        return storageCell.IsTransient ? _zeroValue : LoadFromTree(storageCell);
    }

    /// <summary>
    /// Attempt to get the current value at the storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="bytes">Resulting value</param>
    /// <returns>True if value has been set</returns>
    private bool TryGetCachedStorageValue(in StorageCell storageCell, out byte[]? bytes)
    {
        if (_storageIntraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
        {
            int lastChangeIndex = stack.Peek();
            {
                bytes = _storageChanges[lastChangeIndex]!.Value;
                return true;
            }
        }

        bytes = null;
        return false;
    }

    private byte[] LoadFromTree(in StorageCell storageCell)
    {
        StorageTree tree = GetOrCreateStorage(storageCell.Address);

        Db.Metrics.StorageTreeReads++;
        byte[] value = tree.Get(storageCell.Index);
        PushToRegistryOnly(storageCell, value);
        return value;
    }

    private void PushToRegistryOnly(in StorageCell cell, byte[] value)
    {
        SetupRegistry(cell);
        IncrementChangePosition();
        _storageIntraBlockCache[cell].Push(_currentStoragePosition);
        _originalValues[cell] = value;
        _storageChanges[_currentStoragePosition] = new StorageChange(StorageChangeType.JustCache, cell, value);
    }

    public void ClearStorage(Address address)
    {
        foreach (KeyValuePair<StorageCell, StackList<int>> cellByAddress in _storageIntraBlockCache)
        {
            if (cellByAddress.Key.Address == address)
            {
                Set(cellByAddress.Key, _zeroValue);
            }
        }

        // here it is important to make sure that we will not reuse the same tree when the contract is revived
        // by means of CREATE 2 - notice that the cached trie may carry information about items that were not
        // touched in this block, hence were not zeroed above
        // TODO: how does it work with pruning?
        _storages[address] = new StorageTree(_trieStore, Keccak.EmptyTreeHash, _logManager);
    }

    private void PushUpdate(in StorageCell cell, byte[] value)
    {
        SetupRegistry(cell);
        IncrementChangePosition();
        _storageIntraBlockCache[cell].Push(_currentStoragePosition);
        _storageChanges[_currentStoragePosition] = new StorageChange(StorageChangeType.Update, cell, value);
    }

    /// <summary>
    /// Initialize the StackList at the storage cell position if needed
    /// </summary>
    /// <param name="cell"></param>
    private void SetupRegistry(in StorageCell cell)
    {
        if (!_storageIntraBlockCache.ContainsKey(cell))
        {
            _storageIntraBlockCache[cell] = new StackList<int>();
        }
    }

    /// <summary>
    /// Increment position and size (if needed) of _changes
    /// </summary>
    private void IncrementChangePosition()
    {
        Resettable<StorageChange>.IncrementPosition(ref _storageChanges, ref _storageCapacity, ref _currentStoragePosition);
    }

    private void CommitStorage(IStorageTracer tracer)
    {
        if (_currentStoragePosition == Snapshot.EmptyPosition)
        {
            if (_logger.IsTrace) _logger.Trace("No storage changes to commit");
        }
        else
        {
            CommitCore(tracer);
        }
    }

    /// <summary>
    /// Restore the state to the provided snapshot
    /// </summary>
    /// <param name="snapshot">Snapshot index</param>
    /// <exception cref="InvalidOperationException">Throws exception if snapshot is invalid</exception>
    internal void RestoreStorage(int snapshot)
    {
        if (_logger.IsTrace) _logger.Trace($"Restoring storage snapshot {snapshot}");

        if (snapshot > _currentStoragePosition)
        {
            throw new InvalidOperationException($"{GetType().Name} tried to restore snapshot {snapshot} beyond current position {_currentStoragePosition}");
        }

        if (snapshot == _currentStoragePosition)
        {
            return;
        }

        List<StorageChange> keptInCache = new();

        for (int i = 0; i < _currentStoragePosition - snapshot; i++)
        {
            StorageChange storageChange = _storageChanges[_currentStoragePosition - i];
            if (_storageIntraBlockCache[storageChange!.StorageCell].Count == 1)
            {
                if (_storageChanges[_storageIntraBlockCache[storageChange.StorageCell].Peek()]!.StorageChangeType == StorageChangeType.JustCache)
                {
                    int actualPosition = _storageIntraBlockCache[storageChange.StorageCell].Pop();
                    if (actualPosition != _currentStoragePosition - i)
                    {
                        throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentStoragePosition} - {i}");
                    }

                    keptInCache.Add(storageChange);
                    _storageChanges[actualPosition] = null;
                    continue;
                }
            }

            int forAssertion = _storageIntraBlockCache[storageChange.StorageCell].Pop();
            if (forAssertion != _currentStoragePosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentStoragePosition} - {i}");
            }

            _storageChanges[_currentStoragePosition - i] = null;

            if (_storageIntraBlockCache[storageChange.StorageCell].Count == 0)
            {
                _storageIntraBlockCache.Remove(storageChange.StorageCell);
            }
        }

        _currentStoragePosition = snapshot;
        foreach (StorageChange kept in keptInCache)
        {
            _currentStoragePosition++;
            _storageChanges[_currentStoragePosition] = kept;
            _storageIntraBlockCache[kept.StorageCell].Push(_currentStoragePosition);
        }

        while (_transactionChangesSnapshots.TryPeek(out int lastOriginalSnapshot) && lastOriginalSnapshot > snapshot)
        {
            _transactionChangesSnapshots.Pop();
        }
    }

    private int TakeStorageSnapshot(bool newTransactionStart)
    {
        if (_logger.IsTrace) _logger.Trace($"Storage snapshot {_currentStoragePosition}");
        if (newTransactionStart && _currentStoragePosition != Resettable.EmptyPosition)
        {
            _transactionChangesSnapshots.Push(_currentStoragePosition);
        }
        return _currentStoragePosition;
    }

    /// <summary>
    /// Called by Commit
    /// Used for persistent storage specific logic
    /// </summary>
    /// <param name="tracer">Storage tracer</param>
    private void CommitCore(IStorageTracer tracer)
    {
        if (_logger.IsTrace) _logger.Trace("Committing storage changes");

        if (_storageChanges[_currentStoragePosition] is null)
        {
            throw new InvalidOperationException($"Change at current position {_currentStoragePosition} was null when commiting storage in {nameof(WorldState)}");
        }

        if (_storageChanges[_currentStoragePosition + 1] is not null)
        {
            throw new InvalidOperationException($"Change after current position ({_currentStoragePosition} + 1) was not null when commiting storage in {nameof(WorldState)}");
        }

        HashSet<Address> toUpdateRoots = new HashSet<Address>();

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, StorageChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = new Dictionary<StorageCell, StorageChangeTrace>();
        }

        for (int i = 0; i <= _currentStoragePosition; i++)
        {
            StorageChange storageChange = _storageChanges[_currentStoragePosition - i];
            if ((storageChange!.StorageCell.IsTransient) || (!isTracing && storageChange!.StorageChangeType == StorageChangeType.JustCache))
            {
                continue;
            }

            if (_storageCommittedThisRound.Contains(storageChange!.StorageCell))
            {
                if (isTracing && storageChange.StorageChangeType == StorageChangeType.JustCache)
                {
                    trace![storageChange.StorageCell] = new StorageChangeTrace(storageChange.Value, trace[storageChange.StorageCell].After);
                }

                continue;
            }

            if (isTracing && storageChange.StorageChangeType == StorageChangeType.JustCache)
            {
                tracer!.ReportStorageRead(storageChange.StorageCell);
            }

            _storageCommittedThisRound.Add(storageChange.StorageCell);

            if (storageChange.StorageChangeType == StorageChangeType.Destroy)
            {
                continue;
            }

            int forAssertion = _storageIntraBlockCache[storageChange.StorageCell].Pop();
            if (forAssertion != _currentStoragePosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentStoragePosition} - {i}");
            }

            switch (storageChange.StorageChangeType)
            {
                case StorageChangeType.Destroy:
                    break;
                case StorageChangeType.JustCache:
                    break;
                case StorageChangeType.Update:
                    if (_logger.IsTrace)
                    {
                        _logger.Trace($"  Update {storageChange.StorageCell.Address}_{storageChange.StorageCell.Index} V = {storageChange.Value.ToHexString(true)}");
                    }

                    StorageTree tree = GetOrCreateStorage(storageChange.StorageCell.Address);
                    Db.Metrics.StorageTreeWrites++;
                    toUpdateRoots.Add(storageChange.StorageCell.Address);
                    tree.Set(storageChange.StorageCell.Index, storageChange.Value);
                    if (isTracing)
                    {
                        trace![storageChange.StorageCell] = new StorageChangeTrace(storageChange.Value);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        // TODO: it seems that we are unnecessarily recalculating root hashes all the time in storage?
        foreach (Address address in toUpdateRoots)
        {
            // since the accounts could be empty accounts that are removing (EIP-158)
            if (AccountExists(address))
            {
                Keccak root = RecalculateStorageRoot(address);

                // _logger.Warn($"Recalculating storage root {address}->{root} ({toUpdateRoots.Count})");
                UpdateStorageRoot(address, root);
            }
        }

        Resettable<StorageChange>.Reset(ref _storageChanges, ref _storageCapacity, ref _currentStoragePosition);
        _storageIntraBlockCache.Reset();
        _transactionChangesSnapshots.Clear();

        _originalValues.Reset();
        _storageCommittedThisRound.Reset();

        if (isTracing)
        {
            ReportStorageChanges(tracer!, trace!);
        }
    }

    private static void ReportStorageChanges(IStorageTracer tracer, Dictionary<StorageCell, StorageChangeTrace> trace)
    {
        foreach ((StorageCell address, StorageChangeTrace change) in trace)
        {
            byte[] before = change.Before;
            byte[] after = change.After;

            if (!Bytes.AreEqual(before, after))
            {
                tracer.ReportStorageChange(address, before, after);
            }
        }
    }

    private readonly struct StorageChangeTrace
    {
        public StorageChangeTrace(byte[]? before, byte[]? after)
        {
            After = after ?? _zeroValue;
            Before = before ?? _zeroValue;
        }

        public StorageChangeTrace(byte[]? after)
        {
            After = after ?? _zeroValue;
            Before = _zeroValue;
        }

        public byte[] Before { get; }
        public byte[] After { get; }
    }

    /// <summary>
    /// Used for tracking each change to storage
    /// </summary>
    private class StorageChange
    {
        public StorageChange(StorageChangeType storageChangeType, StorageCell storageCell, byte[] value)
        {
            StorageCell = storageCell;
            Value = value;
            StorageChangeType = storageChangeType;
        }

        public StorageChangeType StorageChangeType { get; }
        public StorageCell StorageCell { get; }
        public byte[] Value { get; }
    }

    /// <summary>
    /// Type of change to track
    /// </summary>
    private enum StorageChangeType
    {
        JustCache,
        Update,
        Destroy,
    }
}
