// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class StateStore
{
    private StateTree _tree;
    private ITrieStore _trieStore;
    private readonly IKeyValueStore _codeDb;
    private ILogManager _logManager;

    public Keccak RootHash
    {
        get => GetStateRoot();
        set => MoveToStateRoot(value);
    }

    public StateStore(ITrieStore? trieStore, IKeyValueStore? codeDb, ILogManager? logManager)
    {
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _tree = new StateTree(trieStore, logManager);
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
    }

    public Account? GetState(Address address) => _tree.Get(address);

    public void SetState(Address address, Account? account) =>  _tree.Set(address, account);

    public StorageTree GetOrCreateStorage(Address address) => new StorageTree(_trieStore, GetStorageRoot(address), _logManager);

    private Keccak GetStorageRoot(Address address)
    {
        Account? account = GetState(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when accessing storage root");
        }
        return account.StorageRoot;
    }

    public void SetStorage(StorageCell storageCell, byte[] value)
    {
        StorageTree tree = GetOrCreateStorage(storageCell.Address);
        tree.Set(storageCell.Index, value);
    }

    public byte[] GetStorage(StorageCell storageCell)
    {
        StorageTree tree = GetOrCreateStorage(storageCell.Address);
        return tree.Get(storageCell.Index);
    }

    public void Commit(long blockNumber)
    {
        _tree.Commit(blockNumber);
    }

    public string Dump()
    {
        TreeDumper dumper = new TreeDumper();
        _tree.Accept(dumper, RootHash);
        return dumper.ToString();
    }

    public TrieStats CollectStats()
    {
        TrieStatsCollector collector = new(_codeDb, _logManager);
        _tree.Accept(collector, RootHash, new VisitingOptions { MaxDegreeOfParallelism = Environment.ProcessorCount });
        return collector.Stats;
    }

    public void MoveToStateRoot(Keccak stateRoot)
    {
        _tree.RootHash = stateRoot;
    }

    public void UpdateRootHash()
    {
        _tree.UpdateRootHash();
    }

    public Keccak GetStateRoot()
    {
        return _tree.RootHash;
    }

    public StorageTree ClearStorage(Address address) => new StorageTree(_trieStore, Keccak.EmptyTreeHash, _logManager);

    public void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
    {
        _tree.Accept(visitor, rootHash, visitingOptions);
    }

}
