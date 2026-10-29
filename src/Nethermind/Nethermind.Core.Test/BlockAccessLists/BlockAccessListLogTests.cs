// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip7928;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class BlockAccessListLogTests
{
    private static readonly Address[] Addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD];
    private static readonly UInt256[] Values = [0, 1, 2, 3];
    private static readonly byte[][] Codes =
    [
        [],
        Bytes.FromHexString("0x6000"),
        Bytes.FromHexString("0x5f5f00"),
        Bytes.FromHexString("0xef0100b94f5374fce5edbc8e2a8697c15331677e6ebf0b"),
    ];

    /// <summary>
    /// Random operation sequences, fed to both <see cref="BlockAccessListAtIndex"/> and a <see cref="BlockAccessIndexRecorder"/>,
    /// must merge into the same block access list, and the recorder's lookups must return the live state after every operation.
    /// </summary>
    /// <remarks>
    /// The indexes are spread over up to three logs, as parallel workers record them, and some are executed once and
    /// discarded first, as block building does with a transaction it leaves out.
    /// </remarks>
    [Test]
    public void Merges_to_the_same_list_as_the_per_index_slices([Range(0, 999)] int seed)
    {
        Random random = new(seed);
        World world = RandomWorld(random);
        int indexCount = random.Next(1, 8);
        SliceMerge expected = new();
        BlockAccessListLog[] logs = new BlockAccessListLog[random.Next(1, 4)];
        BlockAccessIndexRecorder[] recorders = new BlockAccessIndexRecorder[logs.Length];
        for (int worker = 0; worker < logs.Length; worker++)
        {
            logs[worker] = new BlockAccessListLog();
            recorders[worker] = new BlockAccessIndexRecorder { Log = logs[worker] };
        }

        for (uint index = 0; index < indexCount; index++)
        {
            BlockAccessIndexRecorder recorder = recorders[random.Next(recorders.Length)];
            recorder.Begin(index);
            if (random.Next(5) == 0)
            {
                ExecuteIndex(random, world.Clone(), new BlockAccessListAtIndex { Index = index }, recorder);
                recorder.Discard();
            }

            BlockAccessListAtIndex slice = new() { Index = index };
            world = ExecuteIndex(random, world, slice, recorder);
            recorder.End();
            expected.Merge(slice);
        }

        foreach (BlockAccessListLog log in logs) log.Seal();
        GeneratedBlockAccessList merged = GeneratedBlockAccessList.Merge(logs);
        ReadOnlyBlockAccessList expectedList = expected.ToReadOnlyBlockAccessList();
        byte[] expectedRlp = BlockAccessListDecoder.EncodeToBytes(expectedList);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BlockAccessListDecoder.EncodeToBytes(merged), Is.EqualTo(expectedRlp));
            Assert.That(merged.ItemCount, Is.EqualTo(expectedList.ItemCount));
            Assert.That(merged.Matches(BlockAccessListDecoder.Instance.Decode(expectedRlp)!, out Address? mismatched), Is.True, $"mismatch at {mismatched}");
            Assert.That(BlockAccessListLog.Matches(logs, BlockAccessListDecoder.Instance.Decode(expectedRlp)!), Is.True);
        }
    }

    [TestCase(Mutation.None, null)]
    [TestCase(Mutation.StorageValue, "0x00000000000000000000000000000000000000a3")]
    [TestCase(Mutation.ExtraAccount, "0x00000000000000000000000000000000000000a1")]
    [TestCase(Mutation.MissingLastAccount, null)]
    [TestCase(Mutation.MissingChange, "0x00000000000000000000000000000000000000a2")]
    [TestCase(Mutation.UndeclaredRead, "0x00000000000000000000000000000000000000a3")]
    public void Matches_detects_the_first_difference(Mutation mutation, string? mismatchedAddress)
    {
        ReadOnlyBlockAccessList suggested = BlockAccessListDecoder.Instance.Decode(BlockAccessListDecoder.EncodeToBytes(GeneratedBlockAccessList.Merge(BuildLogs(Mutation.None))))!;
        BlockAccessListLog[] logs = BuildLogs(mutation);

        bool matches = GeneratedBlockAccessList.Merge(logs).Matches(suggested, out Address? mismatched);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matches, Is.EqualTo(mutation == Mutation.None));
            Assert.That(mismatched, Is.EqualTo(mismatchedAddress is null ? null : new Address(mismatchedAddress)));
            Assert.That(BlockAccessListLog.Matches(logs, suggested), Is.EqualTo(mutation == Mutation.None));
        }
    }

    public enum Mutation
    {
        None,
        StorageValue,
        ExtraAccount,
        MissingLastAccount,
        MissingChange,
        UndeclaredRead,
    }

    private static BlockAccessListLog[] BuildLogs(Mutation mutation)
    {
        Address a1 = new("0x00000000000000000000000000000000000000a1");
        Address a2 = new("0x00000000000000000000000000000000000000a2");
        Address a3 = new("0x00000000000000000000000000000000000000a3");
        Address a4 = new("0x00000000000000000000000000000000000000a4");

        BlockAccessIndexRecorder first = new();
        first.Begin(1);
        if (mutation == Mutation.MissingChange) first.AddAccountRead(a2);
        else first.AddBalanceChange(a2, 10, 7);
        first.AddStorageChange(new StorageCell(a3, 1), 0, mutation == Mutation.StorageValue ? (UInt256)6 : 5);
        if (mutation == Mutation.ExtraAccount) first.AddAccountRead(a1);

        BlockAccessIndexRecorder second = new();
        second.Begin(2);
        second.AddStorageRead(new StorageCell(a3, 2));
        if (mutation == Mutation.UndeclaredRead) second.AddStorageRead(new StorageCell(a3, 3));
        if (mutation != Mutation.MissingLastAccount) second.AddAccountRead(a4);

        first.End();
        second.End();
        first.Log.Seal();
        second.Log.Seal();
        return [first.Log, second.Log];
    }

    private static World ExecuteIndex(Random random, World world, BlockAccessListAtIndex slice, BlockAccessIndexRecorder log)
    {
        // A storage clear before the index hides a slot's original value, which the writes then seed.
        Dictionary<StorageCell, UInt256> clearedSlots = [];
        if (random.Next(4) == 0)
        {
            StorageCell cleared = RandomCell(random);
            clearedSlots[cleared] = world.GetStorage(cleared);
            world.Storage[cleared] = 0;
        }

        World start = world.Clone();
        Stack<(int Slice, int Log, World World)> snapshots = new();
        int operations = random.Next(0, 24);
        for (int operation = 0; operation < operations; operation++)
        {
            Address address = Addresses[random.Next(Addresses.Length)];
            switch (random.Next(11))
            {
                case 0:
                    slice.AddAccountRead(address);
                    log.AddAccountRead(address);
                    break;
                case 1:
                    {
                        StorageCell cell = RandomCell(random);
                        slice.AddStorageRead(cell);
                        log.AddStorageRead(cell);
                        break;
                    }
                case 2:
                    {
                        UInt256 before = world.GetBalance(address);
                        UInt256 after = random.Next(3) == 0 ? start.GetBalance(address) : Values[random.Next(Values.Length)];
                        slice.AddBalanceChange(address, before, after);
                        log.AddBalanceChange(address, before, after);
                        world.Balances[address] = after;
                        break;
                    }
                case 3:
                    {
                        ulong nonce = world.GetNonce(address) + 1;
                        slice.AddNonceChange(address, nonce);
                        log.AddNonceChange(address, nonce);
                        world.Nonces[address] = nonce;
                        break;
                    }
                case 4:
                    {
                        byte[] before = world.GetCode(address);
                        byte[] after = Codes[random.Next(Codes.Length)];
                        if (random.Next(3) == 0)
                        {
                            ValueHash256 codeHash = ValueKeccak.Compute(after);
                            slice.AddAdoptedCodeChange(address, before, after, codeHash);
                            log.AddAdoptedCodeChange(address, before, after, codeHash);
                        }
                        else
                        {
                            slice.AddCodeChange(address, before, after);
                            log.AddCodeChange(address, before, after);
                        }
                        world.Codes[address] = after;
                        break;
                    }
                case 5 or 6:
                    {
                        StorageCell cell = RandomCell(random);
                        UInt256 before = world.GetStorage(cell);
                        UInt256 after = Values[random.Next(Values.Length)];
                        if (clearedSlots.TryGetValue(cell, out UInt256 beforeClear))
                        {
                            slice.RecordReadAndGet(cell.Address).SeedPreTxStorage(cell.Index, beforeClear);
                            log.SeedPreTxStorage(cell, beforeClear);
                        }
                        slice.AddStorageChange(cell, before, after);
                        log.AddStorageChange(cell, before, after);
                        world.Storage[cell] = after;
                        break;
                    }
                case 7:
                    {
                        bool exists = random.Next(2) == 0;
                        slice.RecordAccountExistence(address, exists);
                        log.RecordAccountExistence(address, exists);
                        world.Exists[address] = exists;
                        break;
                    }
                case 8:
                    snapshots.Push((slice.TakeSnapshot(), log.TakeSnapshot(), world.Clone()));
                    break;
                case 9 when snapshots.TryPop(out (int Slice, int Log, World World) snapshot):
                    slice.Restore(snapshot.Slice);
                    log.Restore(snapshot.Log);
                    world = snapshot.World;
                    break;
                case 10:
                    snapshots.TryPop(out _);
                    break;
            }

            AssertLookups(log, slice, world, start);
        }

        // SELFDESTRUCT finalizes at the end of the transaction, so nothing reads the account after it.
        if (random.Next(6) == 0)
        {
            Address address = Addresses[random.Next(Addresses.Length)];
            slice.DeleteAccount(address, world.GetBalance(address));
            log.DeleteAccount(address, world.GetBalance(address));
            slice.RecordAccountExistence(address, false);
            log.RecordAccountExistence(address, false);
            world.Balances[address] = 0;
        }

        return world;
    }

    private static void AssertLookups(BlockAccessIndexRecorder log, BlockAccessListAtIndex slice, World world, World start)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (Address address in Addresses)
            {
                Assert.That(log.TryGetBalance(address, out UInt256 balance) ? balance : start.GetBalance(address), Is.EqualTo(world.GetBalance(address)), $"balance {address}");
                Assert.That(log.TryGetNonce(address, out ulong nonce) ? nonce : start.GetNonce(address), Is.EqualTo(world.GetNonce(address)), $"nonce {address}");
                Assert.That(log.TryGetCodeChange(address, out CodeChange code) ? code.Code : start.GetCode(address), Is.EqualTo(world.GetCode(address)), $"code {address}");
                Assert.That(log.GetAccountExists(address) ?? start.GetExists(address), Is.EqualTo(world.GetExists(address)), $"existence {address}");
                foreach (UInt256 slot in Values)
                {
                    StorageCell cell = new(address, slot);
                    // A miss means the slot holds its value from before the index, which a storage clear can hide from the world.
                    UInt256 expected = world.GetStorage(cell);
                    UInt256 actual = log.TryGetStorage(cell, out UInt256 value) ? value
                        : log.TryGetPreTxStorage(cell, out UInt256 preTx) && preTx == expected ? preTx
                        : start.GetStorage(cell);
                    Assert.That(actual, Is.EqualTo(expected), $"storage {cell}");
                }
            }
        }
    }

    private static StorageCell RandomCell(Random random) => new(Addresses[random.Next(Addresses.Length)], Values[random.Next(Values.Length)]);

    private static World RandomWorld(Random random)
    {
        World world = new();
        foreach (Address address in Addresses)
        {
            world.Balances[address] = Values[random.Next(Values.Length)];
            world.Nonces[address] = (ulong)random.Next(3);
            world.Codes[address] = Codes[random.Next(Codes.Length)];
            world.Exists[address] = random.Next(2) == 0;
            foreach (UInt256 slot in Values) world.Storage[new StorageCell(address, slot)] = Values[random.Next(Values.Length)];
        }
        return world;
    }

    /// <summary>The block list that per-index slices merge into: each kind of change appended per index, and a slot changed at any index left out of the reads.</summary>
    private sealed class SliceMerge
    {
        private readonly SortedDictionary<Address, Account> _accounts = [];

        public void Merge(BlockAccessListAtIndex slice)
        {
            foreach (AccountChangesAtIndex source in slice.AccountChanges)
            {
                if (!_accounts.TryGetValue(source.Address, out Account? target)) _accounts.Add(source.Address, target = new Account());
                if (source.BalanceChange is { } balance) target.Balances.Add(balance);
                if (source.NonceChange is { } nonce) target.Nonces.Add(nonce);
                if (source.CodeChange is { } code) target.Codes.Add(code);
                foreach ((UInt256 key, StorageChange change) in source.StorageChanges)
                {
                    if (!target.Storage.TryGetValue(key, out List<StorageChange>? changes)) target.Storage.Add(key, changes = []);
                    changes.Add(change);
                    target.Reads.Remove(key);
                }
                foreach (UInt256 read in source.StorageReads)
                {
                    if (!target.Storage.ContainsKey(read)) target.Reads.Add(read);
                }
            }
        }

        public ReadOnlyBlockAccessList ToReadOnlyBlockAccessList()
        {
            List<ReadOnlyAccountChanges> accounts = [];
            int itemCount = 0;
            foreach ((Address address, Account account) in _accounts)
            {
                List<ReadOnlySlotChanges> storage = [];
                foreach ((UInt256 key, List<StorageChange> changes) in account.Storage) storage.Add(new ReadOnlySlotChanges(key, [.. changes]));
                accounts.Add(new ReadOnlyAccountChanges(address, [.. storage], [.. account.Reads], [.. account.Balances], [.. account.Nonces], [.. account.Codes]));
                itemCount += 1 + account.Storage.Count + account.Reads.Count;
            }
            return new ReadOnlyBlockAccessList([.. accounts], itemCount);
        }

        private sealed class Account
        {
            public List<BalanceChange> Balances { get; } = [];
            public List<NonceChange> Nonces { get; } = [];
            public List<CodeChange> Codes { get; } = [];
            public SortedDictionary<UInt256, List<StorageChange>> Storage { get; } = [];
            public SortedSet<UInt256> Reads { get; } = [];
        }
    }

    private sealed class World
    {
        public Dictionary<Address, UInt256> Balances { get; private init; } = [];
        public Dictionary<Address, ulong> Nonces { get; private init; } = [];
        public Dictionary<Address, byte[]> Codes { get; private init; } = [];
        public Dictionary<Address, bool> Exists { get; private init; } = [];
        public Dictionary<StorageCell, UInt256> Storage { get; private init; } = [];

        public UInt256 GetBalance(Address address) => Balances.GetValueOrDefault(address);
        public ulong GetNonce(Address address) => Nonces.GetValueOrDefault(address);
        public byte[] GetCode(Address address) => Codes.GetValueOrDefault(address) ?? [];
        public bool GetExists(Address address) => Exists.GetValueOrDefault(address);
        public UInt256 GetStorage(in StorageCell cell) => Storage.GetValueOrDefault(cell);

        public World Clone() => new()
        {
            Balances = new(Balances),
            Nonces = new(Nonces),
            Codes = new(Codes),
            Exists = new(Exists),
            Storage = new(Storage),
        };
    }
}
