// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using static Paprika.Tests.Store.TreeShapeVisitor;

namespace Paprika.Tests.Store;

public class RangeIteratorTests
{
    private const long Mb = 1024 * 1024;
    private const int Seed = 17;
    private const int AccountKeySize = 20;
    private const int AccountValueSize = 80;
    private const int SlotValueSize = 33;
    private const int RandomRanges = 64;

    /// <summary>The first byte of clustered keys, so that they share the pages under one bucket of the state root page.</summary>
    private const byte Cluster = 0xAB;

    private static readonly byte[] Zero32 = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000");
    private static readonly byte[] Max32 = Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
    private static readonly byte[] Zero20 = Zero32[..AccountKeySize];
    private static readonly byte[] Max20 = Max32[..AccountKeySize];

    public enum Tree
    {
        /// <summary>Accounts sharing their first byte: the data page under the state root page fans out.</summary>
        State,

        /// <summary>The slots of one contract: its subtree fans out.</summary>
        Storage,

        /// <summary>Slots of 31 contracts, which fill the subtrees of ids 1 to 15 and 16 to 31: both become data pages.</summary>
        Group,

        /// <summary>Contracts sharing their first four nibbles: their ids page becomes a data page.</summary>
        Ids,
    }

    [Test]
    public async Task Iteration_matches_model_through_commits([Values(1, 2, 3)] int seed, [Values] bool clusteredKeys)
    {
        const int rounds = 10;
        Random random = new(seed);
        RandomWrites writes = new(random, clusteredKeys);
        Queue<(IVisitableReadOnlyBatch Reader, Model Snapshot, int Round)> held = new();

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        try
        {
            for (int round = 0; round < rounds; round++)
            {
                using (IBatch batch = db.BeginNextBatch())
                {
                    writes.Apply(db, batch);
                    await batch.Commit(CommitOptions.DangerNoWrite);
                }

                writes.Commit();
                IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
                held.Enqueue((read, writes.Committed, round));
                AssertMatches(read, writes.Committed, writes.Contracts, $"after round {round}");
                AssertRandomRanges(read, writes.Committed, writes.Contracts, random, $"after round {round}");

                if (held.Count > 2)
                {
                    (IVisitableReadOnlyBatch old, Model snapshot, int oldRound) = held.Dequeue();
                    AssertMatches(old, snapshot, writes.Contracts, $"for the reader of round {oldRound} after round {round}");
                    old.Dispose();
                }
            }

            Assert.That(writes.Committed.SlotsOf(writes.Contracts[0]), Has.Count.GreaterThan(3_000), "The first contract should outgrow a bottom page");
        }
        finally
        {
            foreach ((IVisitableReadOnlyBatch Reader, Model Snapshot, int Round) entry in held)
            {
                entry.Reader.Dispose();
            }
        }
    }

    [Test]
    public async Task Iteration_matches_model_after_reopen()
    {
        RandomWrites writes = new(new Random(Seed), clusteredKeys: false);

        using TempDirectory dir = new();
        using (PagedDb db = PagedDb.MemoryMappedDb(64 * Mb, 2, dir.Path, accessObserver: null))
        {
            for (int round = 0; round < 3; round++)
            {
                using IBatch batch = db.BeginNextBatch();
                writes.Apply(db, batch);
                await batch.Commit(CommitOptions.FlushDataAndRoot);
                writes.Commit();
            }
        }

        using PagedDb reopened = PagedDb.MemoryMappedDb(64 * Mb, 2, dir.Path, accessObserver: null);
        using IVisitableReadOnlyBatch read = reopened.BeginReadOnlyBatch();
        AssertMatches(read, writes.Committed, writes.Contracts, "after reopening");
    }

    [TestCase(Tree.State, 20_000)]
    [TestCase(Tree.Storage, 25_000)]
    [TestCase(Tree.Group, 150)]
    [TestCase(Tree.Ids, 3_000)]
    public async Task Iterates_across_page_transitions(Tree tree, int count)
    {
        Random random = new(Seed);
        Model model = new();
        byte[][] contracts = tree switch
        {
            Tree.Storage => [NewKey(random, Keccak.Size)],
            Tree.Group => [.. Enumerable.Range(0, 31).Select(_ => NewKey(random, Keccak.Size))],
            _ => [],
        };

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        for (int quarter = 0; quarter < 4; quarter++)
        {
            await db.Commit(batch =>
            {
                for (int i = 0; i < count / 4; i++)
                {
                    switch (tree)
                    {
                        case Tree.State:
                            model.SetAccount(batch, NewKey(random, AccountKeySize, clustered: true), Value(random, AccountValueSize));
                            break;
                        case Tree.Storage:
                            model.SetSlot(batch, contracts[0], NewKey(random, Keccak.Size), Value(random, SlotValueSize));
                            break;
                        case Tree.Group:
                            // Writing the contracts in order gives them ids 1 to 31.
                            foreach (byte[] contract in contracts)
                            {
                                model.SetSlot(batch, contract, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
                            }

                            break;
                        case Tree.Ids:
                            byte[] sharingIdsPage = NewKey(random, Keccak.Size);
                            sharingIdsPage[0] = Cluster;
                            sharingIdsPage[1] = Cluster;
                            model.SetSlot(batch, sharingIdsPage, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
                            break;
                    }
                }
            });

            using IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
            byte[][] all = [.. model.Contracts.Keys];
            AssertMatches(read, model, all, $"after quarter {quarter}");
            AssertRandomRanges(read, model, all.Length > 0 ? all : [Zero32], random, $"after quarter {quarter}");
        }

        TreeShapeVisitor shape = VisitRoot(db);
        Assert.That(tree is Tree.State or Tree.Storage ? shape.FanOutChildren : shape.DataPages, Is.GreaterThan(0), "The pages should have turned");
    }

    [Test]
    public async Task Main_map_entry_shadows_stale_child_copy()
    {
        Random random = new(Seed);
        Model model = new();
        byte[] contract = NewKey(random, Keccak.Size);
        byte[][] slots = [.. Enumerable.Range(0, 600).Select(_ => NewKey(random, Keccak.Size))];

        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        await db.Commit(batch =>
        {
            foreach (byte[] slot in slots)
            {
                model.SetSlot(batch, contract, slot, Value(random, SlotValueSize));
            }
        });

        // All the slots share the contract's nibble, so the subtree's root soon turns into a data page, and the slots live in the
        // bottom page under it. Values of another size are set in that page's main map; the copies in its children go stale.
        await db.Commit(batch =>
        {
            foreach (byte[] slot in slots[..50])
            {
                model.SetSlot(batch, contract, slot, Value(random, 2));
            }
        });

        TreeShapeVisitor shape = VisitRoot(db);
        using (IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(shape.ShadowedChildEntries, Is.GreaterThan(0), "Some child entries should be shadowed");
            Assert.That(ReadStorage(read, contract, Zero32, Max32), Is.EqualTo(Entries(model.SlotsOf(contract))));
        }

        await db.Commit(batch =>
        {
            foreach (byte[] slot in slots[..10])
            {
                model.SetSlot(batch, contract, slot, null);
            }
        });

        using IVisitableReadOnlyBatch afterDelete = db.BeginReadOnlyBatch();
        Assert.That(ReadStorage(afterDelete, contract, Zero32, Max32), Is.EqualTo(Entries(model.SlotsOf(contract))), "After deleting");
    }

    [TestCaseSource(nameof(BoundsCases))]
    public async Task Bounds_are_honoured(bool storage, bool emptyDb, string from, string to, string[] expected)
    {
        byte[] contract = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");

        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb);
        if (!emptyDb)
        {
            await db.Commit(batch =>
            {
                foreach (string key in BoundsKeys)
                {
                    byte[] value = Bytes.FromHexString(key)[^2..];
                    batch.SetRaw(storage ? Key.StorageCell(NibblePath.FromKey(contract), Bytes.FromHexString(key)) : Key.Account(NibblePath.FromKey(Bytes.FromHexString(key))), value);
                }
            });
        }

        using IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
        byte[] lo = Bytes.FromHexString(from);
        byte[] hi = Bytes.FromHexString(to);
        string[] actual = storage ? ReadStorage(read, contract, lo, hi) : ReadState(read, lo, hi);

        Assert.That(actual.Select(entry => "0x" + entry[..64].ToLowerInvariant()), Is.EqualTo(expected));
    }

    private static readonly string[] BoundsKeys =
    [
        "0x0000000000000000000000000000000000000000000000000000000000000000",
        "0x1234000000000000000000000000000000000000000000000000000000000030",
        "0x1234000000000000000000000000000000000000000000000000000000000031",
        "0x123400000000000000000000000000000000000000000000000000000000003f",
        "0x12ff000000000000000000000000000000000000000000000000000000000000",
        "0x1300000000000000000000000000000000000000000000000000000000000000",
        "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
    ];

    private static IEnumerable<TestCaseData> BoundsCases()
    {
        string zero = BoundsKeys[0];
        string max = BoundsKeys[^1];
        (string Name, bool EmptyDb, string From, string To, string[] Expected)[] cases =
        [
            ("empty db", true, zero, max, []),
            ("from above to", false, max, zero, []),
            ("single present key", false, BoundsKeys[2], BoundsKeys[2], [BoundsKeys[2]]),
            ("single absent key", false, "0x1234000000000000000000000000000000000000000000000000000000000032", "0x1234000000000000000000000000000000000000000000000000000000000032", []),
            ("everything", false, zero, max, BoundsKeys),
            ("lowest key only", false, zero, zero, [zero]),
            ("highest key only", false, max, max, [max]),
            ("keys differing in the last nibble", false, BoundsKeys[1], BoundsKeys[3], BoundsKeys[1..4]),
            ("between keys differing in the last nibble", false, "0x1234000000000000000000000000000000000000000000000000000000000032", "0x123400000000000000000000000000000000000000000000000000000000003e", []),
            ("prefix-aligned", false, "0x1200000000000000000000000000000000000000000000000000000000000000", "0x12ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", BoundsKeys[1..5]),
        ];

        foreach (bool storage in (bool[])[false, true])
        {
            foreach ((string name, bool emptyDb, string from, string to, string[] expected) in cases)
            {
                yield return new TestCaseData(storage, emptyDb, from, to, expected).SetName($"Bounds_are_honoured({(storage ? "storage" : "state")}, {name})");
            }
        }
    }

    [Test]
    public async Task Contract_storage_is_isolated_within_a_shared_group([Values(0, 1, 14, 15)] int position)
    {
        // Contracts get ids in the order their storage is first written, from 1 on. Ids 16 to 31 share one storage subtree,
        // where the contract with id 16 + position sits under the first nibble position.
        const int targetId = 16;
        Random random = new(Seed);
        Model model = new();
        byte[][] contracts = [.. Enumerable.Range(0, 32).Select(_ => NewKey(random, Keccak.Size))];
        byte[] target = contracts[targetId + position - 1];

        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        await db.Commit(batch =>
        {
            foreach (byte[] contract in contracts)
            {
                model.SetSlot(batch, contract, Zero32, Value(random, SlotValueSize));
                model.SetSlot(batch, contract, Max32, Value(random, SlotValueSize));
            }

            for (int i = 0; i < 300; i++)
            {
                model.SetSlot(batch, target, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
            }
        });

        using IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(model.Contracts[target]), Is.EqualTo(targetId + position));
            foreach (byte[] contract in contracts)
            {
                Assert.That(ReadStorage(read, contract, Zero32, Max32), Is.EqualTo(Entries(model.SlotsOf(contract))), $"Contract {Convert.ToHexString(contract)}");
            }
        }
    }

    [Test]
    public async Task Concurrent_iterators_on_one_reader_match_model()
    {
        const int threads = 8;
        const int rangesPerThread = 200;
        Random random = new(Seed);
        Model model = new();
        byte[] contract = NewKey(random, Keccak.Size);

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        await db.Commit(batch =>
        {
            for (int i = 0; i < 20_000; i++)
            {
                model.SetAccount(batch, NewKey(random, AccountKeySize, clustered: true), Value(random, AccountValueSize));
            }

            for (int i = 0; i < 2_000; i++)
            {
                model.SetSlot(batch, contract, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
            }
        });

        SortedDictionary<byte[], byte[]> slots = model.SlotsOf(contract);
        byte[][] accountKeys = [.. model.Accounts.Keys];
        byte[][] slotKeys = [.. slots.Keys];
        int[] mismatches = new int[threads];

        using IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
        await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            Random own = new(Seed + thread);
            for (int i = 0; i < rangesPerThread; i++)
            {
                bool storage = i % 2 == 1;
                (byte[] from, byte[] to) = RandomRange(own, storage ? slotKeys : accountKeys, storage ? Keccak.Size : AccountKeySize);
                string[] actual = storage ? ReadStorage(read, contract, from, to) : ReadState(read, from, to);
                if (!actual.SequenceEqual(Entries(storage ? slots : model.Accounts, from, to)))
                {
                    mismatches[thread]++;
                }
            }
        })));

        Assert.That(mismatches, Is.All.EqualTo(0));
    }

    [Test]
    public async Task Reader_iteration_survives_commits_and_drops()
    {
        Random random = new(Seed);
        Model model = new();
        byte[] contract = NewKey(random, Keccak.Size);
        byte[] other = NewKey(random, Keccak.Size);

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        await db.Commit(batch =>
        {
            for (int i = 0; i < 6_000; i++)
            {
                model.SetSlot(batch, contract, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
            }
        });

        string[] expected = Entries(model.SlotsOf(contract));
        List<string> actual = [];

        using IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
        using RangeIterator iterator = read.IterateStorage(contract, Zero32, Max32);
        while (actual.Count < expected.Length / 2 && iterator.MoveNext())
        {
            actual.Add(Entry(iterator.CurrentKey, iterator.CurrentValue));
        }

        await db.Commit(batch => model.DeleteStorage(batch, contract));
        for (int i = 0; i < 5; i++)
        {
            await db.Commit(batch =>
            {
                for (int j = 0; j < 1_200; j++)
                {
                    model.SetSlot(batch, contract, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
                    model.SetSlot(batch, other, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
                }
            });
        }

        while (iterator.MoveNext())
        {
            actual.Add(Entry(iterator.CurrentKey, iterator.CurrentValue));
        }

        using IVisitableReadOnlyBatch latest = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected), "The reader's iteration");
            Assert.That(ReadStorage(latest, contract, Zero32, Max32), Is.EqualTo(Entries(model.SlotsOf(contract))), "A new reader");
        }
    }

    [Test]
    public async Task IterateContracts_yields_contracts_with_storage_in_range()
    {
        Random random = new(Seed);
        Model model = new();

        // Two contracts whose ids page is overwritten below, as an invalid id would read from a file of another version.
        byte[] zeroId = Bytes.FromHexString("0x1234000000000000000000000000000000000000000000000000000000000001");
        byte[] shortId = Bytes.FromHexString("0x1234000000000000000000000000000000000000000000000000000000000002");

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        await db.Commit(batch =>
        {
            model.SetSlot(batch, zeroId, Zero32, Value(random, SlotValueSize));
            model.SetSlot(batch, shortId, Zero32, Value(random, SlotValueSize));
        });

        Page ids = db.UnsafeEnumerateNonRoot().Single(page => page.Header.PageType == PageType.Bottom && page.Header.Level == 0);
        new BottomPage(ids).Map.Set(NibblePath.FromKey(zeroId).SliceFrom(4), Bytes.FromHexString("0x00000000"));
        new BottomPage(ids).Map.Set(NibblePath.FromKey(shortId).SliceFrom(4), Bytes.FromHexString("0x0100"));
        model.Contracts.Remove(zeroId);
        model.Contracts.Remove(shortId);

        byte[][] dropped = [.. Enumerable.Range(0, 100).Select(_ => NewKey(random, Keccak.Size))];
        await db.Commit(batch =>
        {
            for (int i = 0; i < 3_000; i++)
            {
                byte[] clustered = NewKey(random, Keccak.Size);
                clustered[0] = Cluster;
                clustered[1] = Cluster;
                model.SetSlot(batch, clustered, NewKey(random, Keccak.Size), Value(random, SlotValueSize));
            }

            for (int i = 0; i < 300; i++)
            {
                model.SetSlot(batch, NewKey(random, Keccak.Size), NewKey(random, Keccak.Size), Value(random, SlotValueSize));
            }

            foreach (byte[] contract in dropped)
            {
                model.SetSlot(batch, contract, Zero32, Value(random, SlotValueSize));
            }

            // Accounts of the same length without storage have no id.
            for (int i = 0; i < 200; i++)
            {
                batch.SetRaw(Key.Account(NibblePath.FromKey(NewKey(random, Keccak.Size))), Value(random, AccountValueSize));
            }
        });

        // A contract keeps its id once its storage is gone.
        await db.Commit(batch =>
        {
            foreach (byte[] contract in dropped)
            {
                model.DeleteStorage(batch, contract);
            }
        });

        using IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
        byte[][] contracts = [.. model.Contracts.Keys];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadContracts(read, Zero32, Max32), Is.EqualTo(Entries(model.Contracts)), "All contracts");
            for (int i = 0; i < RandomRanges; i++)
            {
                (byte[] from, byte[] to) = RandomRange(random, contracts, Keccak.Size);
                Assert.That(ReadContracts(read, from, to), Is.EqualTo(Entries(model.Contracts, from, to)), $"Contracts in [{Convert.ToHexString(from)}, {Convert.ToHexString(to)}]");
            }
        }
    }

    [Test]
    public async Task Iterates_only_state_keys_of_the_bound_length([Values(1, 2, 19, 20, 32)] int keyBytes)
    {
        Random random = new(Seed);
        int[] lengths = [1, 2, 19, 20, 21, 32];
        Dictionary<int, SortedDictionary<byte[], byte[]>> byLength = lengths.ToDictionary(length => length, _ => new SortedDictionary<byte[], byte[]>(Bytes.Comparer));

        byte[] clusterKey = [Cluster];
        byte[] large = new byte[250];
        large.AsSpan().Fill(0x2a);

        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        await db.Commit(batch =>
        {
            // Keys shorter than a byte live in the state root page's own map.
            batch.SetRaw(Key.Account(NibblePath.Empty), Value(random, AccountValueSize));
            batch.SetRaw(Key.Account(NibblePath.Single(0xA, 0)), Value(random, AccountValueSize));

            // Three-nibble keys are local to the data page under the cluster's bucket, as is the one-byte cluster key. These
            // values overflow its map into a side page.
            for (byte nibble = 0; nibble < 16; nibble++)
            {
                byte[] threeNibbles = [Cluster, (byte)(nibble << NibblePath.NibbleShift)];
                batch.SetRaw(Key.Account(NibblePath.FromKey(threeNibbles, 0, 3)), large);
            }

            foreach (int length in lengths)
            {
                for (int i = 0; i < 300; i++)
                {
                    // One-byte keys spread over the state root page's buckets, including the one the clustered keys share.
                    byte[] key = length == 1 ? [i == 0 ? Cluster : random.NextByte()] : NewKey(random, length, clustered: true);
                    byte[] value = Value(random, AccountValueSize);
                    batch.SetRaw(Key.Account(NibblePath.FromKey(key)), value);
                    byLength[length][key] = value;
                }
            }
        });

        // With a side page, deleting a local key leaves a tombstone in the page's map, which shadows the side page.
        await db.Commit(batch => batch.SetRaw(Key.Account(NibblePath.FromKey(clusterKey)), ReadOnlySpan<byte>.Empty));
        byLength[1].Remove(clusterKey);

        using IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
        byte[] max = new byte[keyBytes];
        max.AsSpan().Fill(0xFF);
        Assert.That(ReadState(read, new byte[keyBytes], max), Is.EqualTo(Entries(byLength[keyBytes])));
    }

    [Test]
    public async Task Invalid_bounds_and_disposed_reader_or_iterator_throw()
    {
        byte[] contract = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");

        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb);
        await db.Commit(batch =>
        {
            batch.SetRaw(Key.Account(NibblePath.FromKey(Zero20)), Bytes.FromHexString("0x2a"));
            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(contract), Zero32), Bytes.FromHexString("0x2a"));
        });

        IVisitableReadOnlyBatch read = db.BeginReadOnlyBatch();
        using RangeIterator state = read.IterateState(Zero20, Max20);
        using RangeIterator storage = read.IterateStorage(contract, Zero32, Max32);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => read.IterateState(Zero20, Max32), Throws.ArgumentException, "Bounds of different lengths");
            Assert.That(() => read.IterateState([], []), Throws.ArgumentException, "Empty bounds");
            Assert.That(() => read.IterateState(new byte[33], new byte[33]), Throws.ArgumentException, "Bounds longer than 32 bytes");
            Assert.That(() => read.IterateStorage(Zero20, Zero32, Max32), Throws.ArgumentException, "A 20-byte account");
            Assert.That(() => read.IterateStorage(contract, Zero20, Max32), Throws.ArgumentException, "A 20-byte slot");
            Assert.That(() => read.IterateContracts(Zero20, Max20), Throws.ArgumentException, "20-byte contracts");
            Assert.That(storage.MoveNext(), Is.True, "Iterating storage before the reader is disposed");
        }

        read.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => state.MoveNext(), Throws.TypeOf<ObjectDisposedException>(), "A new iteration");
            Assert.That(() => storage.MoveNext(), Throws.TypeOf<ObjectDisposedException>(), "A started iteration");
            Assert.That(() => storage.CurrentValue.ToArray(), Throws.TypeOf<ObjectDisposedException>(), "The value of a started iteration");
            Assert.That(() => read.IterateState(Zero20, Max20), Throws.TypeOf<ObjectDisposedException>(), "A new iterator");
        }

        storage.Dispose();
        Assert.That(() => storage.CurrentKey.ToArray(), Throws.TypeOf<ObjectDisposedException>(), "The key of a disposed iterator");
    }

    private static void AssertMatches(IVisitableReadOnlyBatch read, Model expected, byte[][] contracts, string when)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadState(read, Zero20, Max20), Is.EqualTo(Entries(expected.Accounts)), $"Accounts {when}");
            Assert.That(ReadContracts(read, Zero32, Max32), Is.EqualTo(Entries(expected.Contracts)), $"Contracts {when}");
            foreach (byte[] contract in contracts)
            {
                Assert.That(ReadStorage(read, contract, Zero32, Max32), Is.EqualTo(Entries(expected.SlotsOf(contract))), $"Storage of {Convert.ToHexString(contract)} {when}");
            }
        }
    }

    private static void AssertRandomRanges(IVisitableReadOnlyBatch read, Model expected, byte[][] contracts, Random random, string when)
    {
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < RandomRanges; i++)
            {
                int tree = i % 3;
                byte[] contract = contracts[random.Next(2) == 0 ? 0 : random.Next(contracts.Length)];
                SortedDictionary<byte[], byte[]> model = tree switch
                {
                    0 => expected.Accounts,
                    1 => expected.Contracts,
                    _ => expected.SlotsOf(contract),
                };

                (byte[] from, byte[] to) = RandomRange(random, [.. model.Keys], tree == 0 ? AccountKeySize : Keccak.Size);
                string[] actual = tree switch
                {
                    0 => ReadState(read, from, to),
                    1 => ReadContracts(read, from, to),
                    _ => ReadStorage(read, contract, from, to),
                };

                Assert.That(actual, Is.EqualTo(Entries(model, from, to)), $"Tree {tree} in [{Convert.ToHexString(from)}, {Convert.ToHexString(to)}] {when}");
            }
        }
    }

    /// <summary>Reads the accounts in the range; an entry that reads differently through <see cref="IReadOnlyBatch.TryGet"/> is marked.</summary>
    private static string[] ReadState(IVisitableReadOnlyBatch read, byte[] from, byte[] to)
    {
        using RangeIterator iterator = read.IterateState(from, to);
        List<string> entries = [];
        while (iterator.MoveNext())
        {
            bool found = read.TryGet(Key.Account(NibblePath.FromKey(iterator.CurrentKey)), out ReadOnlySpan<byte> value);
            entries.Add(Entry(iterator.CurrentKey, iterator.CurrentValue) + (found && value.SequenceEqual(iterator.CurrentValue) ? "" : " differs from TryGet"));
        }

        return [.. entries];
    }

    /// <summary>Reads the slots in the range; an entry that reads differently through <see cref="IReadOnlyBatch.TryGet"/> is marked.</summary>
    private static string[] ReadStorage(IVisitableReadOnlyBatch read, byte[] contract, byte[] from, byte[] to)
    {
        using RangeIterator iterator = read.IterateStorage(contract, from, to);
        List<string> entries = [];
        while (iterator.MoveNext())
        {
            bool found = read.TryGet(Key.StorageCell(NibblePath.FromKey(contract), iterator.CurrentKey), out ReadOnlySpan<byte> value);
            entries.Add(Entry(iterator.CurrentKey, iterator.CurrentValue) + (found && value.SequenceEqual(iterator.CurrentValue) ? "" : " differs from TryGet"));
        }

        return [.. entries];
    }

    private static string[] ReadContracts(IVisitableReadOnlyBatch read, byte[] from, byte[] to)
    {
        using RangeIterator iterator = read.IterateContracts(from, to);
        List<string> entries = [];
        while (iterator.MoveNext())
        {
            entries.Add(Entry(iterator.CurrentKey, iterator.CurrentValue));
        }

        return [.. entries];
    }

    private static string Entry(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) => $"{Convert.ToHexString(key)}:{Convert.ToHexString(value)}";

    private static string[] Entries(SortedDictionary<byte[], byte[]> model) => [.. model.Select(pair => Entry(pair.Key, pair.Value))];

    private static string[] Entries(SortedDictionary<byte[], byte[]> model, byte[] from, byte[] to) =>
        [.. model.Where(pair => InRange(pair.Key, from, to)).Select(pair => Entry(pair.Key, pair.Value))];

    private static bool InRange(byte[] key, byte[] from, byte[] to) => Bytes.BytesComparer.Compare(key, from) >= 0 && Bytes.BytesComparer.Compare(key, to) <= 0;

    /// <summary>
    /// A range near existing keys: its bounds are existing keys, keys that differ from them in the last nibble or after a
    /// random prefix, the same key twice, or an extreme key.
    /// </summary>
    private static (byte[] From, byte[] To) RandomRange(Random random, byte[][] sortedKeys, int length)
    {
        if (sortedKeys.Length == 0)
        {
            return (new byte[length], Filled(length, 0xFF));
        }

        int at = random.Next(sortedKeys.Length);
        byte[] from = random.Next(16) == 0 ? new byte[length] : Near(random, sortedKeys[at]);
        byte[] to = random.Next(8) switch
        {
            0 => from,
            1 => WithLastNibble(from, 0xF),
            2 when random.Next(4) == 0 => Filled(length, 0xFF),
            _ => Near(random, sortedKeys[Math.Min(at + random.Next(64), sortedKeys.Length - 1)]),
        };

        return Bytes.BytesComparer.Compare(from, to) <= 0 ? (from, to) : (to, from);
    }

    /// <summary>The key itself, the key with another last nibble, or its prefix followed by random bytes.</summary>
    private static byte[] Near(Random random, byte[] key)
    {
        byte[] near = (byte[])key.Clone();
        switch (random.Next(3))
        {
            case 1:
                near[^1] ^= (byte)random.Next(1, 16);
                break;
            case 2:
                random.NextBytes(near.AsSpan(random.Next(1, near.Length)));
                break;
        }

        return near;
    }

    private static byte[] WithLastNibble(byte[] key, int nibble)
    {
        byte[] copy = (byte[])key.Clone();
        copy[^1] = (byte)((copy[^1] & 0xF0) | nibble);
        return copy;
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static byte[] NewKey(Random random, int length, bool clustered = false)
    {
        byte[] key = new byte[length];
        random.NextBytes(key);
        if (clustered)
        {
            key[0] = Cluster;
        }

        return key;
    }

    private static byte[] Value(Random random, int size)
    {
        byte[] value = new byte[size];
        random.NextBytes(value);
        return value;
    }

    /// <summary>The content a store should have: accounts, the slots of each contract, and the id of each contract with storage.</summary>
    private sealed class Model
    {
        private uint _lastId;

        public SortedDictionary<byte[], byte[]> Accounts { get; private init; } = new(Bytes.Comparer);

        /// <summary>The id of each contract whose storage was ever written, 4 bytes little-endian.</summary>
        public SortedDictionary<byte[], byte[]> Contracts { get; private init; } = new(Bytes.Comparer);

        private Dictionary<byte[], SortedDictionary<byte[], byte[]>> Slots { get; init; } = new(Bytes.EqualityComparer);

        public SortedDictionary<byte[], byte[]> SlotsOf(byte[] contract) =>
            Slots.TryGetValue(contract, out SortedDictionary<byte[], byte[]>? slots) ? slots : Slots[contract] = new(Bytes.Comparer);

        public void SetAccount(IBatch batch, byte[] account, byte[]? value)
        {
            batch.SetRaw(Key.Account(NibblePath.FromKey(account)), value);
            if (value is null)
            {
                Accounts.Remove(account);
            }
            else
            {
                Accounts[account] = value;
            }
        }

        public void SetSlot(IBatch batch, byte[] contract, byte[] slot, byte[]? value)
        {
            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(contract), slot), value);
            if (value is null)
            {
                SlotsOf(contract).Remove(slot);
                return;
            }

            SlotsOf(contract)[slot] = value;
            if (!Contracts.ContainsKey(contract))
            {
                byte[] id = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32LittleEndian(id, ++_lastId);
                Contracts[contract] = id;
            }
        }

        public void DeleteStorage(IBatch batch, byte[] contract)
        {
            batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(contract), NibblePath.Empty));
            SlotsOf(contract).Clear();
        }

        /// <summary>Deletes the accounts that start with the first <paramref name="nibbles"/> nibbles of <paramref name="source"/>.</summary>
        public void DeleteAccounts(IBatch batch, byte[] source, int nibbles)
        {
            batch.DeleteByPrefix(Key.Account(NibblePath.FromKey(source).SliceTo(nibbles)));
            foreach (byte[] account in Accounts.Keys.Where(account => NibblePath.FromKey(account).StartsWith(NibblePath.FromKey(source).SliceTo(nibbles))).ToArray())
            {
                Accounts.Remove(account);
            }
        }

        public Model Clone() => new()
        {
            _lastId = _lastId,
            Accounts = new(Accounts, Bytes.Comparer),
            Contracts = new(Contracts, Bytes.Comparer),
            Slots = Slots.ToDictionary(pair => pair.Key, pair => new SortedDictionary<byte[], byte[]>(pair.Value, Bytes.Comparer), Bytes.EqualityComparer),
        };
    }

    /// <summary>
    /// Random writes to a batch, mirrored in <see cref="Current"/>: inserts, overwrites with values of the same or another
    /// size, deletes of present and absent keys, range deletes streamed from a reader of the last commit, drops of whole
    /// contracts, and prefix deletes of accounts.
    /// </summary>
    private sealed class RandomWrites(Random random, bool clusteredKeys)
    {
        private const int OperationsPerRound = 1_500;
        private const int ContractCount = 48;
        private const int OtherAccountValueSize = 40;
        private const int OtherSlotValueSize = 2;

        private readonly List<byte[]> _accounts = [];
        private readonly List<(byte[] Contract, byte[] Slot)> _slots = [];

        /// <summary>The first contract gets most slots. Ids 1 to 48 fill the first three storage subtrees.</summary>
        public byte[][] Contracts { get; } = [.. Enumerable.Range(0, ContractCount).Select(_ => NewKey(random, Keccak.Size, clusteredKeys))];

        public Model Current { get; } = new();

        /// <summary>The model as of the last commit.</summary>
        public Model Committed { get; private set; } = new();

        public void Commit() => Committed = Current.Clone();

        public void Apply(PagedDb db, IBatch batch)
        {
            for (int i = 0; i < OperationsPerRound; i++)
            {
                switch (random.Next(1_000))
                {
                    case < 180:
                        SetAccount(batch, NewKey(random, AccountKeySize, clusteredKeys), Value(random, AccountValueSize));
                        break;
                    case < 230 when _accounts.Count > 0:
                        SetAccount(batch, Pick(_accounts), Value(random, random.Next(2) == 0 ? AccountValueSize : OtherAccountValueSize));
                        break;
                    case < 270 when _accounts.Count > 0:
                        // Present or deleted before.
                        SetAccount(batch, Pick(_accounts), null);
                        break;
                    case < 280:
                        SetAccount(batch, NewKey(random, AccountKeySize, clusteredKeys), null);
                        break;
                    case < 660:
                        SetSlot(batch, (Contracts[0], NewKey(random, Keccak.Size, clusteredKeys)), Value(random, SlotValueSize));
                        break;
                    case < 800:
                        // Skewed, so that the other contracts hold from a few to a few hundred slots.
                        byte[] contract = Contracts[1 + (int)((ContractCount - 1) * Math.Pow(random.NextDouble(), 2))];
                        SetSlot(batch, (contract, NewKey(random, Keccak.Size, clusteredKeys)), Value(random, SlotValueSize));
                        break;
                    case < 880 when _slots.Count > 0:
                        SetSlot(batch, Pick(_slots), Value(random, random.Next(2) == 0 ? SlotValueSize : OtherSlotValueSize));
                        break;
                    case < 940 when _slots.Count > 0:
                        SetSlot(batch, Pick(_slots), null);
                        break;
                    case < 955:
                        SetSlot(batch, (Contracts[random.Next(ContractCount)], NewKey(random, Keccak.Size, clusteredKeys)), null);
                        break;
                    case < 985:
                        DeleteRange(db, batch);
                        break;
                    case < 990:
                        Current.DeleteStorage(batch, Contracts[1 + random.Next(ContractCount - 1)]);
                        break;
                    case < 993 when _accounts.Count > 0:
                        Current.DeleteAccounts(batch, Pick(_accounts), random.Next(1, 6));
                        break;
                    default:
                        SetAccount(batch, NewKey(random, AccountKeySize, clusteredKeys), Value(random, AccountValueSize));
                        break;
                }
            }
        }

        /// <summary>
        /// Deletes the keys that a narrow range held at the last commit, streamed from a reader of it, as the flat layout does.
        /// </summary>
        private void DeleteRange(PagedDb db, IBatch batch)
        {
            bool storage = random.Next(2) == 0;
            byte[] contract = Contracts[random.Next(2) == 0 ? 0 : random.Next(ContractCount)];
            byte[] from = NewKey(random, storage ? Keccak.Size : AccountKeySize, clusteredKeys);
            byte[] to = (byte[])from.Clone();
            to.AsSpan(clusteredKeys ? 2 : 1).Fill(0xFF);

            using (IVisitableReadOnlyBatch committed = db.BeginReadOnlyBatch())
            using (RangeIterator keys = storage ? committed.IterateStorage(contract, from, to) : committed.IterateState(from, to))
            {
                while (keys.MoveNext())
                {
                    batch.SetRaw(storage ? Key.StorageCell(NibblePath.FromKey(contract), keys.CurrentKey) : Key.Account(NibblePath.FromKey(keys.CurrentKey)), ReadOnlySpan<byte>.Empty);
                }
            }

            SortedDictionary<byte[], byte[]> current = storage ? Current.SlotsOf(contract) : Current.Accounts;
            foreach (byte[] key in (storage ? Committed.SlotsOf(contract) : Committed.Accounts).Keys.Where(key => InRange(key, from, to)))
            {
                current.Remove(key);
            }
        }

        private void SetAccount(IBatch batch, byte[] account, byte[]? value)
        {
            Current.SetAccount(batch, account, value);
            _accounts.Add(account);
        }

        private void SetSlot(IBatch batch, (byte[] Contract, byte[] Slot) slot, byte[]? value)
        {
            Current.SetSlot(batch, slot.Contract, slot.Slot, value);
            _slots.Add(slot);
        }

        private T Pick<T>(List<T> touched) => touched[random.Next(touched.Count)];
    }
}
