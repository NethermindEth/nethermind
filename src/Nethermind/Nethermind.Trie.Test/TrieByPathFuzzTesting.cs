// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.ByPathState;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class TrieByPathFuzzTesting
{
    private ILogger _logger;
    private ILogManager _logManager;
    private Random _random = new();

    [SetUp]
    public void SetUp()
    {
        _logManager = LimboLogs.Instance;
        //_logManager = new NUnitLogManager(LogLevel.Trace);
        _logger = _logManager.GetClassLogger();
    }

    [TearDown]
    public void TearDown()
    {
    }

    [TestCase(4, 16, 4, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(128, 263, 24, null)]
    [TestCase(128, 256, 128, null)]
    public void Fuzz_accounts_with_storage(
        int accountsCount,
        int blocksCount,
        int lookupLimit,
        int? seed)
    {
        int usedSeed = seed ?? _random.Next(0, int.MaxValue);
        FuzzTesting(accountsCount, blocksCount, lookupLimit, usedSeed);
    }

    [TestCase(1, null)]
    public void Fuzz_accounts_with_storage_random(int repetition, int? seed)
    {
        for (int i = 0; i < repetition; i++)
        {
            int usedSeed = seed ?? _random.Next(1, int.MaxValue);
            _random = new Random(usedSeed);
            int accountsCount = _random.Next(50, 300);
            int blocksCount = _random.Next(50, 300);
            int lookupLimit = _random.Next(20, 25);
            FuzzTesting(accountsCount, blocksCount, lookupLimit, usedSeed);
        }
    }

    private void FuzzTesting(int accountsCount, int blocksCount, int lookupLimit, int usedSeed)
    {
        _random = new Random(usedSeed);
        Console.WriteLine($"RANDOM SEED {usedSeed}");
        _logger.Info($"RANDOM SEED {usedSeed}");

        string fileName = Path.GetTempFileName();
        Console.WriteLine(
            $"Fuzzing with accounts: {accountsCount}, " +
            $"blocks {blocksCount}, " +
            $"lookup: {lookupLimit} into file {fileName}");

        AccountDecoder decoder = new AccountDecoder();
        using FileStream fileStream = new(fileName, FileMode.Create);
        using StreamWriter streamWriter = new(fileStream);

        Queue<Hash256> rootQueue = new();

        //var dirInfo = Directory.CreateTempSubdirectory();
        //ColumnsDb<StateColumns> memDb = new ColumnsDb<StateColumns>(dirInfo.FullName, new RocksDbSettings("pathState", Path.Combine(dirInfo.FullName, "pathState")), new DbConfig(), _logManager, new StateColumns[] { StateColumns.State, StateColumns.Storage });

        MemColumnsDb<StateColumns> memDb = new();

        var codeDb = new MemDb();
        TestPathPersistanceStrategy strategy = new(lookupLimit, lookupLimit / 2);
        using TrieStoreByPath pathTrieStore = new(new ByPathStateDb(memDb, _logManager), strategy, _logManager);
        WorldState pathStateProvider = new(pathTrieStore, new MemDb(), _logManager);

        using TrieStore trieStore = new(new MemDb(), No.Pruning, Persist.IfBlockOlderThan(lookupLimit), _logManager);
        WorldState stateProvider = new(trieStore, new MemDb(), _logManager);

        Account[] accounts = new Account[accountsCount];
        Address[] addresses = new Address[accountsCount];

        for (int i = 0; i < accounts.Length; i++)
        {
            bool isEmptyValue = _random.Next(0, 2) == 0;
            if (isEmptyValue)
            {
                accounts[i] = Account.TotallyEmpty;
            }
            else
            {
                accounts[i] = TestItem.GenerateRandomAccount();
            }

            addresses[i] = TestItem.GetRandomAddress(_random);
        }

        int BoolToInt(bool val)
        {
            return val ? 1 : 0;
        }
        streamWriter.WriteLine($"{lookupLimit}");
        for (int blockNumber = 0; blockNumber < blocksCount; blockNumber++)
        {
            bool isEmptyBlock = _random.Next(5) == 0;
            streamWriter.WriteLine($"{blockNumber} {BoolToInt(isEmptyBlock)}");
            if (!isEmptyBlock)
            {
                for (int i = 0; i < Math.Max(1, accountsCount / 8); i++)
                {
                    int randomAddressIndex = _random.Next(addresses.Length);
                    int randomAccountIndex = _random.Next(accounts.Length);

                    Address address = addresses[randomAddressIndex];
                    Account account = accounts[randomAccountIndex];
                    streamWriter.WriteLine($"{address} {decoder.Encode(account)}");

                    bool insertStorage = false;
                    bool selfDestruct = false;

                    if (stateProvider.AccountExists(address))
                    {
                        selfDestruct = _random.Next(1, 10) < 3;
                        insertStorage = !selfDestruct;

                        Account existing = stateProvider.GetAccount(address);
                        if (existing.Balance != account.Balance)
                        {
                            if (account.Balance > existing.Balance)
                            {
                                stateProvider.AddToBalance(address, account.Balance - existing.Balance, MuirGlacier.Instance);
                                pathStateProvider.AddToBalance(address, account.Balance - existing.Balance, MuirGlacier.Instance);
                            }
                            else
                            {
                                stateProvider.SubtractFromBalance(address, existing.Balance - account.Balance, MuirGlacier.Instance);
                                pathStateProvider.SubtractFromBalance(address, existing.Balance - account.Balance, MuirGlacier.Instance);
                            }

                            stateProvider.IncrementNonce(address);
                            pathStateProvider.IncrementNonce(address);
                        }
                    }
                    else if (!account.IsTotallyEmpty)
                    {
                        insertStorage = true;
                        stateProvider.CreateAccount(address, account.Balance);
                        pathStateProvider.CreateAccount(address, account.Balance);
                    }

                    if (selfDestruct)
                    {
                        stateProvider.ClearStorage(address);
                        stateProvider.DeleteAccount(address);

                        pathStateProvider.ClearStorage(address);
                        pathStateProvider.DeleteAccount(address);
                    }

                    if (insertStorage)
                    {
                        streamWriter.WriteLine($"{address} {decoder.Encode(account)}");
                        int noOfStorage = _random.Next(1, 50);
                        for (int j = 1; j < noOfStorage + 1; j++)
                        {
                            int index = _random.Next(1, 5000);
                            byte[] storage = new byte[_random.Next(1, 32)];
                            _random.NextBytes(storage);

                            streamWriter.WriteLine($"{index} {storage.ToHexString()}");
                            stateProvider.Set(new StorageCell(address, (UInt256)index), storage);
                            pathStateProvider.Set(new StorageCell(address, (UInt256)index), storage);
                        }
                    }
                }
            }
            stateProvider.Commit(MuirGlacier.Instance);
            stateProvider.CommitTree(blockNumber);
            pathStateProvider.Commit(MuirGlacier.Instance);
            pathStateProvider.CommitTree(blockNumber);

            Assert.That(pathStateProvider.StateRoot, Is.EqualTo(stateProvider.StateRoot), $"State root different at block {blockNumber}");

            rootQueue.Enqueue(stateProvider.StateRoot);

            //finalize block
            if (_random.Next(10) <= 2)
            {
                strategy.AddFinalized(blockNumber, pathStateProvider.StateRoot);
            }

            streamWriter.WriteLine("#");
        }

        streamWriter.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);

        //streamWriter.WriteLine($"DB size: {memDb.Keys.Count}");

        //_logger.Info($"DB size: {memDb.Keys.Count}");
        _logger.Info($"DB path size state: {((IFullDb)memDb.GetColumnDb(StateColumns.State)).Keys.Count}");
        _logger.Info($"DB path size storage: {((IFullDb)memDb.GetColumnDb(StateColumns.Storage)).Keys.Count}");

        //omit blocks until the last persisted block - otherwise cannot compare the history
        int omitted = 0;
        int verifiedBlocks = 0;
        do
        {
            rootQueue.TryDequeue(out Hash256 _);
            omitted++;
        } while (omitted < pathTrieStore.LastPersistedBlockNumber);

        while (rootQueue.TryDequeue(out Hash256 currentRoot))
        {

            stateProvider.Reset();
            pathStateProvider.Reset();
            stateProvider.StateRoot = currentRoot;
            pathStateProvider.StateRoot = currentRoot;

            //TrieStats? stats = pathStateProvider.CollectStats(codeDb, LimboLogs.Instance);
            //Assert.IsTrue(stats.MissingCode == 0);
            //Assert.IsTrue(stats.MissingState == 0);
            //Assert.IsTrue(stats.MissingStorage == 0);
            //Assert.IsTrue(stats.MissingNodes == 0);

            CompareTrees(stateProvider, pathStateProvider);

            verifiedBlocks++;
        }

        //memDb.Dispose();

        Console.WriteLine($"Verified positive {verifiedBlocks}");
        _logger.Info($"Verified positive {verifiedBlocks}");
    }

    private void CompareTrees(WorldState hashState, WorldState pathState)
    {
        TreeDumper dumper = new TreeDumper();
        hashState.Accept(dumper, hashState.StateRoot);
        string remote = dumper.ToString();

        dumper.Reset();

        pathState.Accept(dumper, pathState.StateRoot);
        string local = dumper.ToString();

        Assert.That(local, Is.EqualTo(remote), $"{remote}{Environment.NewLine}{local}");
    }

    private class TestPathPersistanceStrategy : IByPathPersistenceStrategy
    {
        private int _delay;
        private int _interval;
        private long? _lastPersistedBlockNumber;
        public long? LastPersistedBlockNumber { get => _lastPersistedBlockNumber; set => _lastPersistedBlockNumber = value; }

        private SortedList<long, BlockHeader> _finalizedBlocks;

        public TestPathPersistanceStrategy(int delay, int interval)
        {
            _delay = delay;
            _interval = interval;
            _finalizedBlocks = new SortedList<long, BlockHeader>();
        }

        public void AddFinalized(long blockNumber, Hash256 finalizedStateRoot)
        {
            BlockHeader blockHeader = new(Keccak.EmptyTreeHash, Keccak.EmptyTreeHash, TestItem.AddressF, 0, blockNumber, 0, 0, null)
            {
                StateRoot = finalizedStateRoot
            };
            _finalizedBlocks.Add(blockNumber, blockHeader);
        }

        public (long blockNumber, Hash256 stateRoot)? GetBlockToPersist(long currentBlockNumber, Hash256 currentStateRoot)
        {
            long distanceToPersisted = currentBlockNumber - ((_lastPersistedBlockNumber ?? 0) + _delay);

            if (distanceToPersisted > 0 && distanceToPersisted % _interval == 0)
            {
                long targetBlockNumber = currentBlockNumber - _delay;

                for (int i = _finalizedBlocks.Count - 1; i >= 0; i--)
                {
                    if (_finalizedBlocks.GetKeyAtIndex(i) <= targetBlockNumber)
                        return (_finalizedBlocks.GetValueAtIndex(i).Number, _finalizedBlocks.GetValueAtIndex(i).StateRoot);
                }
            }
            return null;
        }
    }
}
