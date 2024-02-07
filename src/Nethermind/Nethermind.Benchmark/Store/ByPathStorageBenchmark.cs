// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Dirichlet.Numerics;
using BenchmarkDotNet.Attributes;
using Nethermind.Trie.Pruning;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.Db.Rocks;
using Nethermind.Config;
using Nethermind.Db.Rocks.Config;
using BenchmarkDotNet.Diagnosers;

namespace Nethermind.Benchmarks.Store;

//[EventPipeProfiler(EventPipeProfile.CpuSampling)]
[ShortRunJob]
public class ByPathStoreReadBenchmarks
{
    private const int pathPoolCount = 100_000;

    private Random _random = new(1876);
    private TrieStore _trieStore;
    private TrieStoreByPath _trieStoreByPath;
    private StateTree _stateTree;
    private StateTreeByPath _stateTreeByPath;

    [Params(100, 1000, 10000)]
    public int NumberOfAccounts;

    private Keccak[] pathPool = new Keccak[pathPoolCount];
    private List<int> _pathsInUse = new List<int>();

    [GlobalSetup]
    public void Setup()
    {
        var stateDBSettings = new RocksDbSettings("testStateDB", "testStateDB") { DeleteOnStart = true };
        var pathStateDBSettings = new RocksDbSettings("pathTestStateDB", "pathTestStateDB") { DeleteOnStart = true };

        ConfigProvider configProvider = new();
        RocksDbFactory dbFactory = new RocksDbFactory(configProvider.GetConfig<IDbConfig>(), NullLogManager.Instance, @"C:\Temp");

        var stateDB = dbFactory.CreateDb(stateDBSettings);
        var stateDB_2 = dbFactory.CreateDb(pathStateDBSettings);

        _trieStore = new TrieStore(stateDB, NullLogManager.Instance);
        _stateTree = new StateTree(_trieStore, NullLogManager.Instance);

        ILeafHistoryStrategy leafHistory = new IndexedLeafHistory();

        _trieStoreByPath = new TrieStoreByPath(stateDB_2, No.Pruning, Persist.EveryBlock, NullLogManager.Instance, leafHistory);
        _stateTreeByPath = new StateTreeByPath(_trieStoreByPath, NullLogManager.Instance);

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Keccak keccak = new Keccak(key);
            pathPool[i] = keccak;
        }

        // generate Remote Tree
        for (int accountIndex = 0; accountIndex < NumberOfAccounts; accountIndex++)
        {
            Account account = TestItem.GenerateRandomAccount(_random);
            int pathKey = _random.Next(pathPool.Length - 1);
            _pathsInUse.Add(pathKey);
            Keccak path = pathPool[pathKey];

            _stateTree.Set(path, account);
            _stateTreeByPath.Set(path, account);
        }
        _stateTree.Commit(100);
        _stateTreeByPath.Commit(100);
    }


    [Benchmark]
    public void TrieStoreGetAccount()
    {
        StateTree localTree = new(_trieStore, NullLogManager.Instance)
        {
            RootHash = _stateTree.RootHash
        };
        for (int i = 0; i < NumberOfAccounts; i++)
        {
            localTree.Get(pathPool[_pathsInUse[i]]);
        }
    }

    [Benchmark]
    public void ByPathTrieStoreGetAccount()
    {
        for (int i = 0; i < NumberOfAccounts; i++)
        {
            _stateTreeByPath.Get(pathPool[_pathsInUse[i]]);
        }
    }
}

[EventPipeProfiler(EventPipeProfile.CpuSampling)]
[ShortRunJob]
public class ByPathStoreCommitBenchmarks
{
    private const int pathPoolCount = 100_000;

    private Random _random = new(1876);
    private TrieStore _trieStore;
    private TrieStoreByPath _trieStoreByPath;
    private StateTree _stateTree;
    private StateTreeByPath _stateTreeByPath;
    private IDb _stateDB;
    private IDb _pathBasedStateDB;

    [Params(100, 1000, 10000)]
    public int NumberOfAccounts;

    private Keccak[] pathPool = new Keccak[pathPoolCount];
    private SortedDictionary<Keccak, Account> accounts = new();

    [GlobalSetup]
    public void Setup()
    {
        var stateDBSettings = new RocksDbSettings("testStateDB", "testStateDB") { DeleteOnStart = true };
        var pathStateDBSettings = new RocksDbSettings("pathTestStateDB", "pathTestStateDB") { DeleteOnStart = true };

        ConfigProvider configProvider = new();
        RocksDbFactory dbFactory = new RocksDbFactory(configProvider.GetConfig<IDbConfig>(), NullLogManager.Instance, @"C:\Temp");

        _stateDB = dbFactory.CreateDb(stateDBSettings);
        _pathBasedStateDB = dbFactory.CreateDb(pathStateDBSettings);

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Keccak keccak = new Keccak(key);
            pathPool[i] = keccak;
        }

        // generate Remote Tree
        for (int accountIndex = 0; accountIndex < NumberOfAccounts; accountIndex++)
        {
            Account account = TestItem.GenerateRandomAccount(_random);
            int pathKey = _random.Next(pathPool.Length - 1);
            Keccak path = pathPool[pathKey];
            accounts[path] = account;
        }
    }


    [Benchmark]
    public void TrieStoreCommit()
    {
        _trieStore = new TrieStore(_stateDB, NullLogManager.Instance);
        _stateTree = new StateTree(_trieStore, NullLogManager.Instance);

        foreach (var accountInfo in accounts)
            _stateTree.Set(accountInfo.Key, accountInfo.Value);

        _stateTree.Commit(1);
    }

    [Benchmark]
    public void ByPathTrieStoreCommit()
    {
        _trieStoreByPath = new TrieStoreByPath(_pathBasedStateDB, No.Pruning, Persist.EveryBlock, NullLogManager.Instance, new IndexedLeafHistory());
        _stateTreeByPath = new StateTreeByPath(_trieStoreByPath, NullLogManager.Instance);

        foreach (var accountInfo in accounts)
            _stateTreeByPath.Set(accountInfo.Key, accountInfo.Value);

        _stateTreeByPath.Commit(1);
    }
}

[ShortRunJob]
public class ByPathStoreHistoryBenchmarks
{
    private const int pathPoolCount = 100_000;

    private Random _random = new(1876);
    private TrieStore _trieStore;
    private TrieStoreByPath _trieStoreByPath;
    private StateTree _stateTree;
    private StateTreeByPath _stateTreeByPath;
    private IDb _stateDB;
    private IDb _pathBasedStateDB;

    [Params(100, 1000, 10000)]
    public int NumberOfAccounts;

    [Params(128, 256)]
    public int NumberOfBlocks;

    private Keccak[] pathPool = new Keccak[pathPoolCount];
    private List<int> _pathsInUse = new List<int>();
    private SortedDictionary<Keccak, Account> _accounts = new();
    private SortedDictionary<long, Keccak> _rootHashes = new();

    [GlobalSetup]
    public void Setup()
    {
        var stateDBSettings = new RocksDbSettings("testStateDB", "testStateDB") { DeleteOnStart = true };
        var pathStateDBSettings = new RocksDbSettings("pathTestStateDB", "pathTestStateDB") { DeleteOnStart = true };

        ConfigProvider configProvider = new();
        RocksDbFactory dbFactory = new RocksDbFactory(configProvider.GetConfig<IDbConfig>(), NullLogManager.Instance, @"C:\Temp");

        _stateDB = dbFactory.CreateDb(stateDBSettings);
        _pathBasedStateDB = dbFactory.CreateDb(pathStateDBSettings);

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Keccak keccak = new Keccak(key);
            pathPool[i] = keccak;
        }

        // generate Remote Tree
        for (int accountIndex = 0; accountIndex < NumberOfAccounts; accountIndex++)
        {
            Account account = TestItem.GenerateRandomAccount(_random);
            int pathKey = _random.Next(pathPool.Length - 1);
            _pathsInUse.Add(pathKey);
            Keccak path = pathPool[pathKey];
            _accounts[path] = account;
        }

        _trieStore = new TrieStore(_stateDB, NullLogManager.Instance);
        _stateTree = new StateTree(_trieStore, NullLogManager.Instance);

        _trieStoreByPath = new TrieStoreByPath(_pathBasedStateDB, No.Pruning, Persist.EveryBlock, NullLogManager.Instance, new IndexedLeafHistory());
        _stateTreeByPath = new StateTreeByPath(_trieStoreByPath, NullLogManager.Instance);

        foreach (var accountInfo in _accounts)
        {
            _stateTree.Set(accountInfo.Key, accountInfo.Value);
            _stateTreeByPath.Set(accountInfo.Key, accountInfo.Value);
        }
        _stateTree.Commit(0);
        _stateTreeByPath.Commit(0);
        _rootHashes[0] = _stateTree.RootHash;

        for (int i = 1; i < NumberOfBlocks; i++)
        {
            int accountsToChange = NumberOfAccounts / 5;
            int modified = 0;
            while (modified < accountsToChange)
            {
                int pathIndex = _random.Next(_pathsInUse.Count - 1);
                Keccak path = pathPool[_pathsInUse[pathIndex]];
                if (_accounts.TryGetValue(path, out Account a))
                {
                    Account aa = a.WithChangedBalance(a.Balance + 3);
                    _stateTree.Set(path, aa);
                    _stateTreeByPath.Set(path, aa);
                    modified++;
                }
            }
            _stateTree.Commit(i);
            _stateTreeByPath.Commit(i);
            if (_stateTree.RootHash != _stateTreeByPath.RootHash)
                throw new Exception("Should not happen!");
            _rootHashes[i] = _stateTree.RootHash;
        }
    }

    [Benchmark]
    public void TrieStoreGetHistory()
    {
        StateTree localTree = new(_trieStore, NullLogManager.Instance)
        {
            RootHash = _stateTree.RootHash
        };

        for (int b = 0; b < 12; b++)
        {
            for (int i = 0; i < NumberOfAccounts; i++)
            {
                localTree.Get(new Address(pathPool[_pathsInUse[i]]), _rootHashes[b]);
            }
        }
    }

    [Benchmark]
    public void ByPathTrieStoreGetHistory()
    {
        for (int b = 0; b < 12; b++)
        {
            for (int i = 0; i < NumberOfAccounts; i++)
            {
                _stateTreeByPath.Get(new Address(pathPool[_pathsInUse[i]]), _rootHashes[b]);
            }
        }
    }

    [ShortRunJob]
    public class ByPathStoreSingleAccountHistoryBenchmarks
    {
        private const int pathPoolCount = 100_000;

        private Random _random = new(1876);
        private TrieStore _trieStore;
        private TrieStoreByPath _trieStoreByPath;
        private StateTree _stateTree;
        private StateTreeByPath _stateTreeByPath;
        private IDb _stateDB;
        private IDb _pathBasedStateDB;

        [Params(100, 1000, 10000)]
        public int NumberOfAccounts;

        [Params(128, 256)]
        public int NumberOfBlocks;

        private Keccak[] pathPool = new Keccak[pathPoolCount];
        private List<int> _pathsInUse = new List<int>();
        private SortedDictionary<Keccak, Account> _accounts = new();
        private SortedDictionary<long, Keccak> _rootHashes = new();

        [GlobalSetup]
        public void Setup()
        {
            var stateDBSettings = new RocksDbSettings("testStateDB", "testStateDB") { DeleteOnStart = true };
            var pathStateDBSettings = new RocksDbSettings("pathTestStateDB", "pathTestStateDB") { DeleteOnStart = true };

            ConfigProvider configProvider = new();
            RocksDbFactory dbFactory = new RocksDbFactory(configProvider.GetConfig<IDbConfig>(), NullLogManager.Instance, @"C:\Temp");

            _stateDB = dbFactory.CreateDb(stateDBSettings);
            _pathBasedStateDB = dbFactory.CreateDb(pathStateDBSettings);

            for (int i = 0; i < pathPoolCount; i++)
            {
                byte[] key = new byte[32];
                ((UInt256)i).ToBigEndian(key);
                Keccak keccak = new Keccak(key);
                pathPool[i] = keccak;
            }

            // generate Remote Tree
            for (int accountIndex = 0; accountIndex < NumberOfAccounts; accountIndex++)
            {
                Account account = TestItem.GenerateRandomAccount(_random);
                int pathKey = _random.Next(pathPool.Length - 1);
                _pathsInUse.Add(pathKey);
                Keccak path = pathPool[pathKey];
                _accounts[path] = account;
            }

            _trieStore = new TrieStore(_stateDB, NullLogManager.Instance);
            _stateTree = new StateTree(_trieStore, NullLogManager.Instance);

            _trieStoreByPath = new TrieStoreByPath(_pathBasedStateDB, No.Pruning, Persist.EveryBlock, NullLogManager.Instance, new IndexedLeafHistory());
            _stateTreeByPath = new StateTreeByPath(_trieStoreByPath, NullLogManager.Instance);

            foreach (var accountInfo in _accounts)
            {
                _stateTree.Set(accountInfo.Key, accountInfo.Value);
                _stateTreeByPath.Set(accountInfo.Key, accountInfo.Value);
            }
            _stateTree.Commit(0);
            _stateTreeByPath.Commit(0);
            _rootHashes[0] = _stateTree.RootHash;

            int toChangeIndex = _pathsInUse[87];
            Keccak pathToChange = pathPool[toChangeIndex];

            for (int i = 1; i < NumberOfBlocks; i++)
            {
                if (_accounts.TryGetValue(pathToChange, out Account a))
                {
                    Account aa = a.WithChangedBalance((ulong)i * 3);
                    _stateTree.Set(pathToChange, aa);
                    _stateTreeByPath.Set(pathToChange, aa);
                }
                _stateTree.Commit(i);
                _stateTreeByPath.Commit(i);
                if (_stateTree.RootHash != _stateTreeByPath.RootHash)
                    throw new Exception("Should not happen!");
                _rootHashes[i] = _stateTree.RootHash;
            }
        }

        [Benchmark]
        public void TrieStoreGetHistorySingle()
        {
            StateTree localTree = new(_trieStore, NullLogManager.Instance)
            {
                RootHash = _stateTree.RootHash
            };

            for (int b = 0; b < 12; b++)
            {
                for (int i = 0; i < NumberOfAccounts; i++)
                {
                    localTree.Get(new Address(pathPool[_pathsInUse[87]]), _rootHashes[b]);
                }
            }
        }

        [Benchmark]
        public void ByPathTrieStoreGetHistorySingle()
        {
            for (int b = 0; b < 12; b++)
            {
                for (int i = 0; i < NumberOfAccounts; i++)
                {
                    _stateTreeByPath.Get(new Address(pathPool[_pathsInUse[87]]), _rootHashes[b]);
                }
            }
        }
    }
}
