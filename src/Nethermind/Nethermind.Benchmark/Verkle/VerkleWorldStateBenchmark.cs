// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Benchmarks.Verkle;

public class VerkleWorldStateBenchmark
{

    [Params(TreeTypeEnum.Verkle, TreeTypeEnum.MPT)]
    public TreeTypeEnum TreeType { get; set; }

    [Params(
        ConfigurationEnum.Balance,
        ConfigurationEnum.WriteHeavy,
        ConfigurationEnum.ReadHeavy,
        ConfigurationEnum.AddressOnly
    )]
    public ConfigurationEnum Configuration { get; set; }

    private AccountDecoder _accountDecoder = new();

    private const int _largerEntryCount = 1024 * 10;
    private (Op, Address, UInt256?, byte[])[] _largerEntriesAccess;

    enum Op
    {
        Read,
        Write
    }

    public enum TreeTypeEnum
    {
        MPT,
        Verkle
    }

    public enum ConfigurationEnum
    {
        Balance,
        WriteHeavy,
        ReadHeavy,
        AddressOnly
    }

    [GlobalSetup]
    public void Setup()
    {
        double storageAccessProbability;
        double storageExistingReadProbability;
        double storageExistingWriteProbability;
        double addressExistingReadProbability;
        double addressExistingWriteProbability;

        switch (Configuration)
        {
            case ConfigurationEnum.Balance:
                storageAccessProbability = 0.9;
                storageExistingReadProbability = 0.3;
                storageExistingWriteProbability = 0.3;
                addressExistingReadProbability = 0.3;
                addressExistingWriteProbability = 0.3;
                break;
            case ConfigurationEnum.WriteHeavy:
                storageAccessProbability = 0.9;
                storageExistingReadProbability = 0.1;
                storageExistingWriteProbability = 0.5;
                addressExistingReadProbability = 0.1;
                addressExistingWriteProbability = 0.5;
                break;
            case ConfigurationEnum.ReadHeavy:
                storageAccessProbability = 0.9;
                storageExistingReadProbability = 0.9;
                storageExistingWriteProbability = 0.1;
                addressExistingReadProbability = 0.9;
                addressExistingWriteProbability = 0.1;
                break;
            case ConfigurationEnum.AddressOnly:
                storageAccessProbability = 0.0;
                storageExistingReadProbability = 0.0;
                storageExistingWriteProbability = 0.0;
                addressExistingReadProbability = 0.3;
                addressExistingWriteProbability = 0.3;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        // Preparing access for large entries
        List<Address> currentAddress = new();
        Dictionary<Address, List<UInt256>> currentSlots = new();
        Dictionary<Address, List<UInt256>> storageAccessHistory = new();
        _largerEntriesAccess = new (Op, Address, UInt256?, byte[])[_largerEntryCount];

        Random rand = new Random(0);
        for (int i = 0; i < _largerEntryCount; i++)
        {
            if (rand.NextDouble() < storageAccessProbability && currentAddress.Count != 0)
            {
                double prob = rand.NextDouble();
                Address address = currentAddress[(int)(rand.NextInt64() % currentAddress.Count)];
                if (!currentSlots.TryGetValue(address, out List<UInt256> currentSlotsForAddress))
                {
                    currentSlotsForAddress = new List<UInt256>();
                    currentSlots[address] = currentSlotsForAddress;
                }
                if (!storageAccessHistory.TryGetValue(address, out List<UInt256> accessHistory))
                {
                    accessHistory = new List<UInt256>();
                    storageAccessHistory[address] = accessHistory;
                }

                if (prob < storageExistingReadProbability && currentSlotsForAddress.Count != 0)
                {
                    // Its an existing read
                    UInt256 slot = DetermineSlotToAccess(accessHistory, rand);

                    accessHistory.Add(slot);
                    _largerEntriesAccess[i] = (
                        Op.Read,
                        address,
                        slot,
                        null);
                }
                else if (prob < storageExistingWriteProbability && currentSlotsForAddress.Count != 0)
                {
                    UInt256 slot = DetermineSlotToAccess(accessHistory, rand);

                    accessHistory.Add(slot);
                    _largerEntriesAccess[i] = (
                        Op.Write,
                        address,
                        slot,
                        Keccak.Compute(i.ToBigEndianByteArray()).BytesToArray());
                }
                else
                {
                    // Its a new write
                    // Uses the total slot address as the address which causes the storage to be dense.
                    // Which is not really, realistic, but need to somehow make the slots dense as verkle is somewhat
                    // optimized for that.
                    UInt256 newSlot = new UInt256((ulong)currentSlotsForAddress.Count);
                    accessHistory.Add(newSlot);

                    currentSlotsForAddress.Add(newSlot);
                    _largerEntriesAccess[i] = (
                        Op.Write,
                        address,
                        newSlot,
                        Keccak.Compute(i.ToBigEndianByteArray()).BytesToArray());
                }
            }
            else
            {
                double prob = rand.NextDouble();
                if (prob < addressExistingReadProbability && currentAddress.Count != 0)
                {
                    // Its an existing read
                    _largerEntriesAccess[i] = (
                        Op.Read,
                        currentAddress[(int)(rand.NextInt64() % currentAddress.Count)],
                        null,
                        null);
                }
                else if (prob < addressExistingWriteProbability && currentAddress.Count != 0)
                {
                    // Its an existing write
                    Address addr = currentAddress[(int)(rand.NextInt64() % currentAddress.Count)];
                    if (currentSlots.ContainsKey(addr))
                    {
                        // Can't change account of an address with slots as it will break storage root.
                        i--;
                        continue;
                    }

                    Account newAccount = new Account((UInt256)rand.NextInt64(), (UInt256)rand.NextInt64());
                    _largerEntriesAccess[i] = (
                        Op.Write,
                        addr,
                        null,
                        _accountDecoder.Encode(newAccount).Bytes);
                }
                else
                {
                    // Its a new write
                    Address newAddress = new Address(Keccak.Compute(i.ToBigEndianByteArray()));
                    Account newAccount = new Account((UInt256)rand.NextInt64(), (UInt256)rand.NextInt64());
                    currentAddress.Add(newAddress);
                    _largerEntriesAccess[i] = (
                        Op.Write,
                        newAddress,
                        null,
                        _accountDecoder.Encode(newAccount).Bytes);
                }
            }
        }
    }

    private static UInt256 DetermineSlotToAccess(List<UInt256> accessHistory, Random rand)
    {
        // Try to get a somewhat recently accessed slot. Verkle is optimized for this kind of scenario.
        // It has no effect on MPT. Obviously, I'm just winging it here.

        double fallbackProb = 0.1; // But sometimes, we just wanna break the pattern a bit.

        int idx = accessHistory.Count - (int)Math.Abs(SampleGaussian(rand, 0, Math.Max(accessHistory.Count * 0.1, 50)));
        if (idx >= accessHistory.Count || idx < 0 || rand.NextDouble() < fallbackProb) idx = (int)(rand.NextInt64() % accessHistory.Count);
        UInt256 slot = accessHistory[idx];
        return slot;
    }

    // Copied from https://gist.github.com/tansey/1444070
    public static double SampleGaussian(Random random, double mean, double stddev)
    {
        // The method requires sampling from a uniform random of (0,1]
        // but Random.NextDouble() returns a sample of [0,1).
        double x1 = 1 - random.NextDouble();
        double x2 = 1 - random.NextDouble();

        double y1 = Math.Sqrt(-2.0 * Math.Log(x1)) * Math.Cos(2.0 * Math.PI * x2);
        return y1 * stddev + mean;
    }

    private IWorldState CreateVerkleWorldState()
    {
        IDbProvider dbProvider = new DbProvider();
        dbProvider.RegisterColumnDb(DbNames.VerkleState, new MemColumnsDb<VerkleDbColumns>());
        dbProvider.RegisterDb(DbNames.StateRootToBlock, new MemDb());
        dbProvider.RegisterDb(DbNames.Code, new MemDb());
        var LogManager = NullLogManager.Instance;
        var TrieStore = new VerkleTreeStore<VerkleSyncCache>(dbProvider, LogManager);
        var State = new VerkleWorldState(TrieStore, dbProvider.CodeDb, LogManager);

        State.Commit(Prague.Instance);
        State.CommitTree(0);
        return State;
    }

    private IWorldState CreatePMTWorldState()
    {
        TrieStore trieStore = new TrieStore(new MemDb(), NullLogManager.Instance);
        return new WorldState(trieStore, new MemDb(), NullLogManager.Instance);
    }

    private IWorldState CreateWorldState()
    {
        return TreeType == TreeTypeEnum.Verkle ? CreateVerkleWorldState() : CreatePMTWorldState();
    }

    [Benchmark]
    public void InsertAndCommitRepeatedlyTimes()
    {
        IWorldState worldState = CreateWorldState();

        for (int i = 0; i < _largerEntryCount; i++)
        {
            if (i % 2000 == 0)
            {
                worldState.Commit(Prague.Instance);
                worldState.CommitTree(i / 2000);
            }

            (Op op, Address address, UInt256? slot, byte[] value) = _largerEntriesAccess[i];

            if (op == Op.Write)
            {
                if (slot == null)
                {
                    Account asAccount = _accountDecoder.Decode(value);
                    worldState.CreateAccount(address, asAccount.Balance, asAccount.Nonce);
                }
                else
                {
                    worldState.Set(new StorageCell(address, slot.Value), value);
                }
            }
            else
            {
                if (slot == null)
                {
                    worldState.GetAccount(address);
                }
                else
                {
                    worldState.Get(new StorageCell(address, slot.Value));
                }
            }
        }
    }
}
