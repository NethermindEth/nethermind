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
        // new NUnitLogManager(LogLevel.Trace);
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
        _random = new Random(usedSeed);
        _logger.Info($"RANDOM SEED {usedSeed}");

        string fileName = Path.GetTempFileName();
        //string fileName = "C:\\Temp\\fuzz.txt";
        _logger.Info(
            $"Fuzzing with accounts: {accountsCount}, " +
            $"blocks {blocksCount}, " +
            $"lookup: {lookupLimit} into file {fileName}");

        AccountDecoder decoder = new AccountDecoder();
        using FileStream fileStream = new(fileName, FileMode.Create);
        using StreamWriter streamWriter = new(fileStream);

        Queue<Keccak> rootQueue = new();

        MemColumnsDb<StateColumns> memDb = new();

        using TrieStoreByPath trieStore = new(memDb, No.Pruning, Persist.EveryBlock, _logManager, lookupLimit);
        using TrieStoreByPath storageTrieStore = new(memDb.GetColumnDb(StateColumns.Storage), No.Pruning, Persist.EveryBlock, _logManager, lookupLimit);
        WorldState stateProvider = new(trieStore, new MemDb(), _logManager);
        //StateProvider stateProvider = new StateProvider(trieStore, storageTrieStore, new MemDb(), _logManager);
        //StorageProvider storageProvider = new StorageProvider(trieStore, stateProvider, _logManager);

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
                    if (stateProvider.AccountExists(address))
                    {
                        insertStorage = true;
                        Account existing = stateProvider.GetAccount(address);
                        if (existing.Balance != account.Balance)
                        {
                            if (account.Balance > existing.Balance)
                            {
                                stateProvider.AddToBalance(
                                    address, account.Balance - existing.Balance, MuirGlacier.Instance);
                            }
                            else
                            {
                                stateProvider.SubtractFromBalance(
                                    address, existing.Balance - account.Balance, MuirGlacier.Instance);
                            }

                            stateProvider.IncrementNonce(address);
                        }
                    }
                    else if (!account.IsTotallyEmpty)
                    {
                        insertStorage = true;
                        stateProvider.CreateAccount(address, account.Balance);
                    }

                    if (insertStorage)
                    {
                        streamWriter.WriteLine($"{address} {decoder.Encode(account)}");
                        int noOfStorage = _random.Next(50);
                        for (int j = 1; j < noOfStorage + 1; j++)
                        {
                            int index = _random.Next(1, 5000);
                            byte[] storage = new byte[_random.Next(1,32)];
                            _random.NextBytes(storage);
                            streamWriter.WriteLine($"{index} {storage.ToHexString()}");
                            stateProvider.Set(new StorageCell(address, (UInt256)index), storage);
                        }
                    }
                }
            }
            stateProvider.Commit(MuirGlacier.Instance);
            stateProvider.CommitTree(blockNumber);
            rootQueue.Enqueue(stateProvider.StateRoot);
            streamWriter.WriteLine("#");
        }

        streamWriter.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);

        // streamWriter.WriteLine($"DB size: {memDb.Keys.Count}");
        _logger.Info($"DB size: {memDb.Keys.Count}");

        int verifiedBlocks = 0;

        while (rootQueue.TryDequeue(out Keccak currentRoot))
        {
            try
            {
                stateProvider.StateRoot = currentRoot;
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (stateProvider.AccountExists(addresses[i]))
                    {
                        for (int j = 0; j < 256; j++)
                        {
                            stateProvider.Get(new StorageCell(addresses[i], (UInt256)j));
                        }
                    }
                }

                _logger.Info($"Verified positive {verifiedBlocks}");
            }
            catch (Exception ex)
            {
                if (verifiedBlocks % lookupLimit == 0)
                {
                    throw new InvalidDataException(ex.ToString());
                }
                else
                {
                    _logger.Info($"Verified negative {verifiedBlocks} which is ok here");
                }
            }

            verifiedBlocks++;
        }
    }

    [TestCase(1, null)]
    public void Fuzz_accounts_with_storage_random(int repetition, int? seed)
    {
        for (int i = 0; i < repetition; i++)
        {
            int usedSeed = seed?? _random.Next(1, int.MaxValue);
            FuzzTesting(usedSeed);
        }
    }

    private void FuzzTesting(int usedSeed)
    {
        _random = new Random(usedSeed);
        Console.WriteLine(usedSeed);
        _logger.Info($"RANDOM SEED {usedSeed}");

        int accountsCount = _random.Next(50, 500);
        int blocksCount = _random.Next(50, 500);
        int lookupLimit = _random.Next(500);

        string fileName = Path.GetTempFileName();
        //string fileName = "C:\\Temp\\fuzz.txt";
        Console.WriteLine(
            $"Fuzzing with accounts: {accountsCount}, " +
            $"blocks {blocksCount}, " +
            $"lookup: {lookupLimit} into file {fileName}");

        AccountDecoder decoder = new AccountDecoder();
        using FileStream fileStream = new(fileName, FileMode.Create);
        using StreamWriter streamWriter = new(fileStream);

        Queue<Keccak> rootQueue = new();

        MemColumnsDb<StateColumns> memDb = new();

        using TrieStoreByPath trieStore = new(memDb, No.Pruning, Persist.EveryBlock, _logManager, blocksCount + 1);
        using TrieStoreByPath storageTrieStore = new(memDb.GetColumnDb(StateColumns.Storage), No.Pruning, Persist.EveryBlock, _logManager, lookupLimit);
        var codeDb = new MemDb();
        WorldState stateProvider = new(trieStore, new MemDb(), _logManager);
        //StateProvider stateProvider = new StateProvider(trieStore, storageTrieStore, codeDb, _logManager);
        //StorageProvider storageProvider = new StorageProvider(storageTrieStore, stateProvider, _logManager);

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
                    if (stateProvider.AccountExists(address))
                    {
                        insertStorage = true;
                        Account existing = stateProvider.GetAccount(address);
                        if (existing.Balance != account.Balance)
                        {
                            if (account.Balance > existing.Balance)
                            {
                                stateProvider.AddToBalance(
                                    address, account.Balance - existing.Balance, MuirGlacier.Instance);
                            }
                            else
                            {
                                stateProvider.SubtractFromBalance(
                                    address, existing.Balance - account.Balance, MuirGlacier.Instance);
                            }

                            stateProvider.IncrementNonce(address);
                        }
                    }
                    else if (!account.IsTotallyEmpty)
                    {
                        insertStorage = true;
                        stateProvider.CreateAccount(address, account.Balance);
                    }

                    if (insertStorage)
                    {
                        streamWriter.WriteLine($"{address} {decoder.Encode(account)}");
                        int noOfStorage = _random.Next(50);
                        for (int j = 1; j < noOfStorage + 1; j++)
                        {
                            int index = _random.Next(1, 5000);
                            byte[] storage = new byte[_random.Next(1,32)];
                            _random.NextBytes(storage);
                            streamWriter.WriteLine($"{index} {storage.ToHexString()}");
                            stateProvider.Set(new StorageCell(address, (UInt256)index), storage);
                        }
                    }
                }
            }
            stateProvider.Commit(MuirGlacier.Instance);
            stateProvider.CommitTree(blockNumber);
            rootQueue.Enqueue(stateProvider.StateRoot);
            streamWriter.WriteLine("#");
        }

        streamWriter.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);

        // streamWriter.WriteLine($"DB size: {memDb.Keys.Count}");
        _logger.Info($"DB size: {memDb.Keys.Count}");

        int verifiedBlocks = 0;

        while (rootQueue.TryDequeue(out Keccak currentRoot))
        {
            try
            {
                stateProvider.StateRoot = currentRoot;
                TrieStats? stats = stateProvider.CollectStats(codeDb, LimboLogs.Instance);
                Assert.IsTrue(stats.MissingCode == 0);
                Assert.IsTrue(stats.MissingState == 0);
                Assert.IsTrue(stats.MissingStorage == 0);
                Assert.IsTrue(stats.MissingNodes == 0);
                foreach (Address t in addresses)
                {
                    if (stateProvider.AccountExists(t))
                    {
                        for (int j = 0; j < 256; j++)
                        {
                            stateProvider.Get(new StorageCell(t, (UInt256)j));
                        }
                    }
                }

                _logger.Info($"Verified positive {verifiedBlocks}");
            }
            catch (Exception ex)
            {
                if (verifiedBlocks % lookupLimit == 0)
                {
                    throw new InvalidDataException(ex.ToString());
                }
                else
                {
                    _logger.Info($"Verified negative {verifiedBlocks} which is ok here");
                }
                throw;
            }

            verifiedBlocks++;
        }
    }
}
