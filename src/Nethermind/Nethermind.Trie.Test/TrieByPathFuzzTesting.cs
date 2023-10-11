// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State;
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
        //_logManager = new NUnitLogManager(LogLevel.Info);
        _logger = _logManager.GetClassLogger();
    }

    [TearDown]
    public void TearDown()
    {
    }

    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(96, 192, 96, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(128, 256, 128, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(4, 16, 4, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
    [TestCase(8, 32, 8, null)]
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

        Queue<Keccak> rootQueue = new();

        MemColumnsDb<StateColumns> memDb = new();

        var codeDb = new MemDb();
        using TrieStoreByPath pathTrieStore = new(memDb, Persist.IfBlockOlderThan(lookupLimit), _logManager);
        WorldState pathStateProvider = new(pathTrieStore, new MemDb(), _logManager);

        using TrieStore trieStore = new(memDb, No.Pruning, Persist.IfBlockOlderThan(lookupLimit), _logManager);
        WorldState stateProvider = new(trieStore, new MemDb(), _logManager);

        Account[] accounts = new Account[accountsCount];
        Address[] addresses = new Address[accountsCount];
        Dictionary<Address, List<int>> indexesPerAccount = new();

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
                    if (stateProvider.AccountExists(address))
                    {
                        insertStorage = true;
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

                    if (insertStorage)
                    {
                        streamWriter.WriteLine($"{address} {decoder.Encode(account)}");
                        int noOfStorage = _random.Next(50, 50);
                        if (!indexesPerAccount.ContainsKey(address))
                            indexesPerAccount[address] = new List<int>(noOfStorage);
                        for (int j = 1; j < noOfStorage + 1; j++)
                        {
                            int index = _random.Next(1, 5000);
                            indexesPerAccount[address].Add(index);
                            byte[] storage = new byte[_random.Next(1,32)];
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
            streamWriter.WriteLine("#");
        }

        streamWriter.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);

        streamWriter.WriteLine($"DB size: {memDb.Keys.Count}");

        _logger.Info($"DB size: {memDb.Keys.Count}");
        _logger.Info($"DB path size state: {((IFullDb)memDb.GetColumnDb(StateColumns.State)).Keys.Count}");
        _logger.Info($"DB path size storage: {((IFullDb)memDb.GetColumnDb(StateColumns.Storage)).Keys.Count}");

        //omit blocks until the last persisted block - otherwise cannot compare the history
        int omitted = 0;
        int verifiedBlocks = 0;
        do
        {
            rootQueue.TryDequeue(out Keccak _);
            omitted++;
        } while (omitted < pathTrieStore.LastPersistedBlockNumber);

        while (rootQueue.TryDequeue(out Keccak currentRoot))
        {

            stateProvider.Reset();
            pathStateProvider.Reset();
            stateProvider.StateRoot = currentRoot;
            pathStateProvider.StateRoot = currentRoot;

            TrieStats? stats = pathStateProvider.CollectStats(codeDb, LimboLogs.Instance);
            Assert.IsTrue(stats.MissingCode == 0);
            Assert.IsTrue(stats.MissingState == 0);
            Assert.IsTrue(stats.MissingStorage == 0);
            Assert.IsTrue(stats.MissingNodes == 0);
            foreach (Address t in addresses)
            {
                if (stateProvider.AccountExists(t))
                {
                    foreach (int index in indexesPerAccount[t])
                    {
                        byte[] value = stateProvider.Get(new StorageCell(t, (UInt256)index));
                        byte[] pathValue = pathStateProvider.Get(new StorageCell(t, (UInt256)index));
                        Assert.That(pathValue, Is.EqualTo(value).Using(Bytes.EqualityComparer), $"Storage slot at {t}.{index} incorrect");
                    }
                }
            }
            verifiedBlocks++;
        }
        _logger.Info($"Verified positive {verifiedBlocks}");
    }
}
