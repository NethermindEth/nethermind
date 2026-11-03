// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

/// <summary>
/// Writes the same random batches to <see cref="FlatLayout.Flat"/> and <see cref="FlatLayout.PaprikaFlat"/>, both with the
/// trie node log, and reads them back alike after every round.
/// </summary>
/// <remarks>
/// Each batch issues its range deletes and self-destructs before its other writes, as every production caller does:
/// PaprikaFlat applies them to the state before the batch, so a write issued ahead of one would survive it there, unlike in Flat.
/// </remarks>
public class PaprikaFlatParityTests
{
    private const int Rounds = 6;
    private const int BatchesPerRound = 5;
    private const int OperationsPerBatch = 100;
    private const int RandomRanges = 32;
    private const int PoolSize = 32;

    private enum BatchKind
    {
        Block,
        Import,
        Sync,
    }

    [Test]
    public void Flat_and_PaprikaFlat_read_alike_after_every_round([Values(1, 2)] int seed)
    {
        Random random = new(seed);
        Address[] addresses = [.. Enumerable.Range(0, PoolSize).Select(static i => new Address(Keccak.Compute($"address {i}")))];
        // The raw writes share the paths of the addresses and their slots, so raw and address writes overwrite each other.
        ValueHash256[] accountPaths = [.. addresses.Select(static address => address.ToAccountPath), .. RandomPaths(random)];
        ValueHash256[] slotPaths = [.. Enumerable.Range(0, PoolSize).Select(static i => SlotPath((UInt256)i)), .. RandomPaths(random)];

        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new();
        using SnapshotableMemColumnsDb<FlatDbColumns> paprikaDb = new();
        using LayoutPersistence flat = new(flatDb, FlatLayout.Flat, trieNodeLog: true);
        using LayoutPersistence paprika = new(paprikaDb, FlatLayout.PaprikaFlat, trieNodeLog: false);

        StateId state = StateId.PreGenesis;
        ulong block = 0;
        for (int round = 0; round < Rounds; round++)
        {
            for (int i = 0; i < BatchesPerRound; i++)
            {
                BatchKind kind = (BatchKind)random.Next(3);
                StateId from = kind == BatchKind.Sync ? StateId.Sync : state;
                StateId to = kind == BatchKind.Sync ? StateId.Sync : new StateId(++block, ValueKeccak.Compute($"state {block}"));
                WriteFlags flags = kind == BatchKind.Block ? WriteFlags.None : WriteFlags.DisableWAL;
                List<Action<IPersistence.IWriteBatch>> operations = Operations(random, addresses, accountPaths, slotPaths);

                Write(flat.Persistence, from, to, flags, operations);
                Write(paprika.Persistence, from, to, flags, operations);
                if (kind != BatchKind.Sync) state = to;
            }

            AssertReadAlike(flat.Persistence, paprika.Persistence, random, accountPaths, slotPaths, state, round);
        }
    }

    private static void Write(IPersistence persistence, in StateId from, in StateId to, WriteFlags flags, List<Action<IPersistence.IWriteBatch>> operations)
    {
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(from, to, flags);
        foreach (Action<IPersistence.IWriteBatch> operation in operations) operation(batch);
    }

    /// <summary>The writes of one batch: a few range deletes and self-destructs, then point writes and deletes.</summary>
    private static List<Action<IPersistence.IWriteBatch>> Operations(Random random, Address[] addresses, ValueHash256[] accountPaths, ValueHash256[] slotPaths)
    {
        List<Action<IPersistence.IWriteBatch>> operations = [];
        int deletes = random.Next(1, 5);
        for (int i = 0; i < deletes; i++)
        {
            (ValueHash256 from, ValueHash256 to) = Range(random, accountPaths);
            (ValueHash256 slotFrom, ValueHash256 slotTo) = Range(random, slotPaths);
            ValueHash256 account = AccountPath(random, accountPaths);
            Address address = addresses[random.Next(addresses.Length)];
            operations.Add(random.Next(4) switch
            {
                0 => batch => batch.DeleteAccountRange(from, to),
                1 => batch => batch.DeleteStorageRange(account, slotFrom, slotTo),
                2 => batch => batch.SelfDestructRange(from, to),
                _ => batch => batch.SelfDestruct(address),
            });
        }

        for (int i = deletes; i < OperationsPerBatch; i++)
        {
            Address address = addresses[random.Next(addresses.Length)];
            ValueHash256 account = AccountPath(random, accountPaths);
            ValueHash256 slot = slotPaths[random.Next(slotPaths.Length)];
            UInt256 slotIndex = (UInt256)random.Next(PoolSize);
            Account value = RandomAccount(random);
            byte[] word = RandomWord(random);
            operations.Add(random.Next(8) switch
            {
                0 => batch => batch.SetAccount(address, null),
                1 => batch => batch.SetAccount(address, value),
                2 => batch => batch.SetAccountRaw(account, value),
                3 => batch => batch.SetStorage(address, slotIndex, null),
                4 or 5 => batch => batch.SetStorage(address, slotIndex, new UInt256(word, isBigEndian: true)),
                _ => batch => batch.SetStorageRawEncoded(account, slot, Rlp.Encode(word).Bytes),
            });
        }

        return operations;
    }

    private static void AssertReadAlike(IPersistence flat, IPersistence paprika, Random random, ValueHash256[] accountPaths, ValueHash256[] slotPaths, StateId state, int round)
    {
        using IPersistence.IPersistenceReader flatReader = flat.CreateReader();
        using IPersistence.IPersistenceReader paprikaReader = paprika.CreateReader();
        List<(ValueHash256 Key, string Value)> accounts = Accounts(flatReader, ValueKeccak.Zero, ValueKeccak.MaxValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flatReader.CurrentState, Is.EqualTo(state), $"round {round}: Flat state pointer");
            Assert.That(paprikaReader.CurrentState, Is.EqualTo(state), $"round {round}: PaprikaFlat state pointer");
            Assert.That(accounts, Is.Not.Empty, $"round {round}: accounts to compare");
            Assert.That(Accounts(paprikaReader, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.EqualTo(accounts), $"round {round}: all accounts");

            int slotCount = 0;
            foreach (ValueHash256 account in accountPaths)
            {
                Assert.That(paprikaReader.GetAccountRaw(account), Is.EqualTo(flatReader.GetAccountRaw(account)), $"round {round}: account {account}");
                List<(ValueHash256 Key, string Value)> slots = Slots(flatReader, account, ValueKeccak.Zero, ValueKeccak.MaxValue);
                slotCount += slots.Count;
                Assert.That(Slots(paprikaReader, account, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.EqualTo(slots), $"round {round}: all slots of {account}");
                foreach (ValueHash256 slot in slotPaths)
                {
                    Assert.That(Slot(paprikaReader, account, slot), Is.EqualTo(Slot(flatReader, account, slot)), $"round {round}: slot {slot} of {account}");
                }
            }

            Assert.That(slotCount, Is.Positive, $"round {round}: slots to compare");

            for (int i = 0; i < RandomRanges; i++)
            {
                (ValueHash256 from, ValueHash256 to) = Range(random, accountPaths);
                Assert.That(Accounts(paprikaReader, from, to), Is.EqualTo(Accounts(flatReader, from, to)), $"round {round}: accounts in [{from}, {to})");

                ValueHash256 account = AccountPath(random, accountPaths);
                (from, to) = Range(random, slotPaths);
                Assert.That(Slots(paprikaReader, account, from, to), Is.EqualTo(Slots(flatReader, account, from, to)), $"round {round}: slots of {account} in [{from}, {to}]");
            }
        }
    }

    private static List<(ValueHash256 Key, string Value)> Accounts(IPersistence.IPersistenceReader reader, in ValueHash256 from, in ValueHash256 to)
    {
        using IPersistence.IFlatIterator iterator = reader.CreateAccountIterator(from, to);
        return PaprikaFlatTestDb.Collect(iterator);
    }

    private static List<(ValueHash256 Key, string Value)> Slots(IPersistence.IPersistenceReader reader, in ValueHash256 account, in ValueHash256 from, in ValueHash256 to)
    {
        using IPersistence.IFlatIterator iterator = reader.CreateStorageIterator(account, from, to);
        return PaprikaFlatTestDb.Collect(iterator);
    }

    private static (bool Found, UInt256 Value) Slot(IPersistence.IPersistenceReader reader, in ValueHash256 account, in ValueHash256 slot)
    {
        UInt256 value = default;
        return (reader.TryGetStorageRaw(account, slot, ref value), value);
    }

    private static ValueHash256 SlotPath(in UInt256 slot)
    {
        ValueHash256 path = default;
        StorageTree.ComputeKeyWithLookup(slot, ref path);
        return path;
    }

    private static ValueHash256[] RandomPaths(Random random) => [.. Enumerable.Range(0, PoolSize).Select(_ => RandomPath(random))];

    private static ValueHash256 RandomPath(Random random)
    {
        ValueHash256 path = default;
        random.NextBytes(path.BytesAsSpan);
        return path;
    }

    /// <summary>A path of <paramref name="accountPaths"/>, its bytes past the 20 that key an account at times randomised.</summary>
    private static ValueHash256 AccountPath(Random random, ValueHash256[] accountPaths)
    {
        ValueHash256 path = accountPaths[random.Next(accountPaths.Length)];
        return random.Next(4) == 0 ? WithRandomTail(random, path, 20) : path;
    }

    private static ValueHash256 WithRandomTail(Random random, ValueHash256 path, int keptBytes)
    {
        random.NextBytes(path.BytesAsSpan[keptBytes..]);
        return path;
    }

    /// <summary>A range whose bounds are the extremes, paths of <paramref name="pool"/> or random paths, so they fall both on and between keys.</summary>
    /// <remarks>Half the ranges keep their bounds' first byte, so they hold a few keys rather than a share of all of them.</remarks>
    private static (ValueHash256 From, ValueHash256 To) Range(Random random, ValueHash256[] pool)
    {
        ValueHash256 first = Bound();
        ValueHash256 second = random.Next(2) == 0 ? Bound() : WithRandomTail(random, first, 1);
        return first.CompareTo(second) <= 0 ? (first, second) : (second, first);

        ValueHash256 Bound() => random.Next(6) switch
        {
            0 => ValueKeccak.Zero,
            1 => ValueKeccak.MaxValue,
            2 or 3 => pool[random.Next(pool.Length)],
            _ => RandomPath(random),
        };
    }

    private static Account RandomAccount(Random random) => random.Next(2) == 0
        ? new Account((ulong)random.Next(4), (UInt256)random.Next(4))
        : new Account((ulong)random.Next(1000), (UInt256)random.NextInt64(), Keccak.Compute($"storage {random.Next()}"), Keccak.Compute($"code {random.Next()}"));

    /// <summary>A slot value's minimal big-endian bytes, empty for zero.</summary>
    private static byte[] RandomWord(Random random)
    {
        byte[] word = new byte[random.Next(16) == 0 ? 0 : random.Next(1, 33)];
        random.NextBytes(word);
        if (word.Length > 0) word[0] = (byte)random.Next(1, 256);
        return word;
    }
}
