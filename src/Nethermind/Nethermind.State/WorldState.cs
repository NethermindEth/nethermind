// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldState : PartialStorageProviderBase, IWorldState
{
    private readonly StateProvider _stateProvider;
    private readonly TransientStorageProvider _transientStorageProvider;

    private readonly ITrieStore _trieStore;
    private readonly ILogManager? _logManager;
    private readonly ResettableDictionary<Address, StorageTree> _storages = new();
    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new();
    private readonly ResettableHashSet<StorageCell> _committedThisRound = new();

    public WorldState(ITrieStore? trieStore, IKeyValueStore? codeDb, ILogManager? logManager) : base(logManager)
    {
        _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _logManager = logManager;
        _stateProvider = new StateProvider(trieStore, codeDb, logManager);
        _transientStorageProvider = new TransientStorageProvider(logManager);
    }

    public Keccak StateRoot
    {
        get => _stateProvider.StateRoot;
        set => _stateProvider.StateRoot = value;
    }

    public Account GetAccount(Address address)
    {
        return _stateProvider.GetAccount(address);
    }

    public void RecalculateStateRoot()
    {
        _stateProvider.RecalculateStateRoot();
    }


    public void DeleteAccount(Address address)
    {
        _stateProvider.DeleteAccount(address);
    }

    public void CreateAccount(Address address, in UInt256 balance)
    {
        _stateProvider.CreateAccount(address, in balance);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce)
    {
        _stateProvider.CreateAccount(address, in balance, in nonce);
    }

    public void InsertCode(Address address, ReadOnlyMemory<byte> code, IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        _stateProvider.InsertCode(address, code, releaseSpec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _stateProvider.AddToBalance(address, in balanceChange, spec);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _stateProvider.SubtractFromBalance(address, in balanceChange, spec);
    }

    public void UpdateStorageRoot(Address address, Keccak storageRoot)
    {
        _stateProvider.UpdateStorageRoot(address, storageRoot);
    }

    public void IncrementNonce(Address address)
    {
        _stateProvider.IncrementNonce(address);
    }

    public void DecrementNonce(Address address)
    {
        _stateProvider.DecrementNonce(address);
    }

    public void TouchCode(Keccak codeHash)
    {
        _stateProvider.TouchCode(codeHash);
    }

    public UInt256 GetNonce(Address address)
    {
        return _stateProvider.GetNonce(address);
    }

    public UInt256 GetBalance(Address address)
    {
        return _stateProvider.GetBalance(address);
    }

    public Keccak GetStorageRoot(Address address)
    {
        return _stateProvider.GetStorageRoot(address);
    }

    public byte[] GetCode(Address address)
    {
        return _stateProvider.GetCode(address);
    }

    public byte[] GetCode(Keccak codeHash)
    {
        return _stateProvider.GetCode(codeHash);
    }

    public Keccak GetCodeHash(Address address)
    {
        return _stateProvider.GetCodeHash(address);
    }

    public void Accept(ITreeVisitor visitor, Keccak stateRoot, VisitingOptions? visitingOptions = null)
    {
        _stateProvider.Accept(visitor, stateRoot, visitingOptions);
    }

    public bool AccountExists(Address address)
    {
        return _stateProvider.AccountExists(address);
    }

    public bool IsDeadAccount(Address address)
    {
        return _stateProvider.IsDeadAccount(address);
    }

    public bool IsEmptyAccount(Address address)
    {
        return _stateProvider.IsEmptyAccount(address);
    }

    public void SetNonce(Address address, in UInt256 nonce)
    {
        _stateProvider.SetNonce(address, nonce);
    }

    public override void ClearStorage(Address address)
    {
        base.ClearStorage(address);

        // here it is important to make sure that we will not reuse the same tree when the contract is revived
        // by means of CREATE 2 - notice that the cached trie may carry information about items that were not
        // touched in this block, hence were not zeroed above
        // TODO: how does it work with pruning?
        _storages[address] = new StorageTree(_trieStore, Keccak.EmptyTreeHash, _logManager);
        _transientStorageProvider.ClearStorage(address);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        base.Commit((IStorageTracer)NullStateTracer.Instance);
        _transientStorageProvider.Commit();
        _stateProvider.Commit(releaseSpec, isGenesis);
    }

    public void Commit(IReleaseSpec releaseSpec, IStateTracer? stateTracer, bool isGenesis = false)
    {
        _stateProvider.Commit(releaseSpec, stateTracer, isGenesis);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? tracer, bool isGenesis = false)
    {
        _stateProvider.Commit(releaseSpec, tracer, isGenesis);
        base.Commit(tracer ?? (IStorageTracer)NullStateTracer.Instance);
        _transientStorageProvider.Commit(tracer ?? (IStorageTracer)NullStateTracer.Instance);
    }

    /// <summary>
    /// Commit persisent storage trees
    /// </summary>
    /// <param name="blockNumber">Current block number</param>
    public void CommitTree(long blockNumber)
    {
        // _logger.Warn($"Storage block commit {blockNumber}");
        foreach (KeyValuePair<Address, StorageTree> storage in _storages)
        {
            storage.Value.Commit(blockNumber);
        }

        // TODO: maybe I could update storage roots only now?

        // only needed here as there is no control over cached storage size otherwise
        _storages.Reset();

        _stateProvider.CommitTree(blockNumber);
    }

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    public byte[] GetOriginal(in StorageCell storageCell)
    {
        if (!_originalValues.ContainsKey(storageCell))
        {
            throw new InvalidOperationException("Get original should only be called after get within the same caching round");
        }

        if (_transactionChangesSnapshots.TryPeek(out int snapshot))
        {
            if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
            {
                if (stack.TryGetSearchedItem(snapshot, out int lastChangeIndexBeforeOriginalSnapshot))
                {
                    return _changes[lastChangeIndexBeforeOriginalSnapshot]!.Value;
                }
            }
        }

        return _originalValues[storageCell];
    }

    public byte[] GetTransientState(in StorageCell storageCell)
    {
        return _transientStorageProvider.Get(storageCell);
    }

    public override void Reset()
    {
        base.Reset();
        _storages.Reset();
        _originalValues.Clear();
        _committedThisRound.Clear();
        _transientStorageProvider.Reset();
        _stateProvider.Reset();
    }

    public void Restore(Snapshot snapshot)
    {
        _stateProvider.Restore(snapshot.StateSnapshot);
        base.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
        _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
    }

    internal void RestoreState(int snapshot) => _stateProvider.Restore(snapshot);
    internal void RestoreStorage(Snapshot.Storage snapshot)
    {
        base.Restore(snapshot.PersistentStorageSnapshot);
        _transientStorageProvider.Restore(snapshot.TransientStorageSnapshot);
    }
    internal void RestoreStorage(int snapshot)
    {
        RestoreStorage(new Snapshot.Storage(snapshot, Snapshot.EmptyPosition));
    }

    public new void Restore(int snapshot)
    {
        _stateProvider.Restore(snapshot);
        base.Restore(snapshot);
        _transientStorageProvider.Restore(snapshot);
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        _transientStorageProvider.Set(storageCell, newValue);
    }

    protected override byte[] GetCurrentValue(in StorageCell storageCell) =>
        TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : LoadFromTree(storageCell);

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
        _intraBlockCache[cell].Push(_currentPosition);
        _originalValues[cell] = value;
        _changes[_currentPosition] = new Change(ChangeType.JustCache, cell, value);
    }

    public new Snapshot TakeSnapshot(bool newTransactionStart)
    {
        int persistentSnapshot = base.TakeSnapshot(newTransactionStart);
        int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
        Snapshot.Storage storageSnapshot = new(persistentSnapshot, transientSnapshot);
        return new Snapshot(_stateProvider.TakeSnapshot(false), storageSnapshot);
    }

    /// <summary>
    /// Called by Commit
    /// Used for persistent storage specific logic
    /// </summary>
    /// <param name="tracer">Storage tracer</param>
    protected override void CommitCore(IStorageTracer tracer)
    {
        if (_logger.IsTrace) _logger.Trace("Committing storage changes");

        if (_changes[_currentPosition] is null)
        {
            throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(PartialStorageProviderBase)}");
        }

        if (_changes[_currentPosition + 1] is not null)
        {
            throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(PartialStorageProviderBase)}");
        }

        HashSet<Address> toUpdateRoots = new();

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, ChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = new Dictionary<StorageCell, ChangeTrace>();
        }

        for (int i = 0; i <= _currentPosition; i++)
        {
            Change change = _changes[_currentPosition - i];
            if (!isTracing && change!.ChangeType == ChangeType.JustCache)
            {
                continue;
            }

            if (_committedThisRound.Contains(change!.StorageCell))
            {
                if (isTracing && change.ChangeType == ChangeType.JustCache)
                {
                    trace![change.StorageCell] = new ChangeTrace(change.Value, trace[change.StorageCell].After);
                }

                continue;
            }

            if (isTracing && change.ChangeType == ChangeType.JustCache)
            {
                tracer!.ReportStorageRead(change.StorageCell);
            }

            _committedThisRound.Add(change.StorageCell);

            if (change.ChangeType == ChangeType.Destroy)
            {
                continue;
            }

            int forAssertion = _intraBlockCache[change.StorageCell].Pop();
            if (forAssertion != _currentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
            }

            switch (change.ChangeType)
            {
                case ChangeType.Destroy:
                    break;
                case ChangeType.JustCache:
                    break;
                case ChangeType.Update:
                    if (_logger.IsTrace)
                    {
                        _logger.Trace($"  Update {change.StorageCell.Address}_{change.StorageCell.Index} V = {change.Value.ToHexString(true)}");
                    }

                    StorageTree tree = GetOrCreateStorage(change.StorageCell.Address);
                    Db.Metrics.StorageTreeWrites++;
                    toUpdateRoots.Add(change.StorageCell.Address);
                    tree.Set(change.StorageCell.Index, change.Value);
                    if (isTracing)
                    {
                        trace![change.StorageCell] = new ChangeTrace(change.Value);
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
            if (_stateProvider.AccountExists(address))
            {
                Keccak root = RecalculateRootHash(address);

                // _logger.Warn($"Recalculating storage root {address}->{root} ({toUpdateRoots.Count})");
                _stateProvider.UpdateStorageRoot(address, root);
            }
        }

        base.CommitCore(tracer);
        _originalValues.Reset();
        _committedThisRound.Reset();

        if (isTracing)
        {
            ReportChanges(tracer!, trace!);
        }
    }

    private Keccak RecalculateRootHash(Address address)
    {
        StorageTree storageTree = GetOrCreateStorage(address);
        storageTree.UpdateRootHash();
        return storageTree.RootHash;
    }

    private StorageTree GetOrCreateStorage(Address address)
    {
        if (!_storages.ContainsKey(address))
        {
            StorageTree storageTree = new(_trieStore, _stateProvider.GetStorageRoot(address), _logManager);
            return _storages[address] = storageTree;
        }

        return _storages[address];
    }

    private static void ReportChanges(IStorageTracer tracer, Dictionary<StorageCell, ChangeTrace> trace)
    {
        foreach ((StorageCell address, ChangeTrace change) in trace)
        {
            byte[] before = change.Before;
            byte[] after = change.After;

            if (!Bytes.AreEqual(before, after))
            {
                tracer.ReportStorageChange(address, before, after);
            }
        }
    }
}
