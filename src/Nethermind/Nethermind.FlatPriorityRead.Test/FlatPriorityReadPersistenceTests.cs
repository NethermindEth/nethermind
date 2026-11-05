// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.FlatPriorityRead.Test;

[Parallelizable(ParallelScope.All)]
public class FlatPriorityReadPersistenceTests
{
    private static readonly Address PriorityA = TestItem.AddressA;
    private static readonly Address PriorityB = TestItem.AddressB;
    private static readonly Address Other = TestItem.AddressC;
    private static readonly ValueHash256 LowerHalfEnd = new("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public static IEnumerable<MirrorCase> Changes =>
    [
        new("SetStorage", persistence => Write(persistence, batch =>
        {
            batch.SetStorage(PriorityA, 1, 7);
            batch.SetStorage(PriorityA, 2, null);
            batch.SetStorage(PriorityA, 9, UInt256.Zero);
        })),
        new("SetStorageRawEncoded", persistence => Write(persistence, batch =>
        {
            batch.SetStorageRawEncoded(PriorityA.ToAccountPath, SlotHash(1), Rlp.Encode(new byte[] { 0x2a }).Bytes);
            batch.SetStorageRawEncoded(PriorityB.ToAccountPath, SlotHash(9), Rlp.Encode(new byte[] { 0x01, 0x00 }).Bytes);
        })),
        new("SelfDestruct then revive", persistence => Write(persistence, batch =>
        {
            batch.SelfDestruct(PriorityA);
            batch.SetStorage(PriorityA, 5, 0x55);
        })),
        new("DeleteStorageRange", persistence => Write(persistence, batch =>
            batch.DeleteStorageRange(PriorityA.ToAccountPath, ValueKeccak.Zero, new ValueHash256("0x8000000000000000000000000000000000000000000000000000000000000000")))),
        new("SelfDestructRange", persistence => Write(persistence, batch =>
            batch.SelfDestructRange(PriorityB.ToAccountPath, PriorityB.ToAccountPath))),
        new("Clear", persistence => persistence.Clear()),
    ];

    public static IEnumerable<MirrorCase> TrieChanges =>
    [
        new("SelfDestruct", persistence => Write(persistence, batch => batch.SelfDestruct(PriorityA))),
        new("DeleteStorageTrieNodeRange", persistence => Write(persistence, batch =>
            batch.DeleteStorageTrieNodeRange(PriorityA.ToAccountPath, ValueKeccak.Zero, LowerHalfEnd))),
        new("SelfDestructRange", persistence => Write(persistence, batch =>
            batch.SelfDestructRange(PriorityB.ToAccountPath, PriorityB.ToAccountPath))),
        new("SetStorageTrieNode with no RLP", persistence => Write(persistence, batch =>
            batch.SetStorageTrieNode(PriorityA.ToAccountPath.ToCommitment(), TreePath.Empty, default))),
        new("Clear", persistence => persistence.Clear()),
    ];

    [Test]
    public void Serves_priority_slots_from_the_store_and_keeps_reader_snapshots([Values] FlatPriorityReadBackend backend, [Values] bool preimage)
    {
        using Fixture fixture = new(backend, preimage, cacheTrie: false, batch =>
        {
            batch.SetStorage(PriorityA, 1, 1);
            batch.SetStorage(PriorityA, 2, UInt256.Zero);
            batch.SetStorage(PriorityB, 1, 0x0b);
            batch.SetStorage(Other, 1, 5);
        });

        using IPersistence.IPersistenceReader before = fixture.Priority.CreateReader();
        AssertSlots(before, (PriorityA, 1, 1), (PriorityA, 2, UInt256.Zero), (PriorityA, 3, null), (PriorityB, 1, 0x0b));
        Assert.That(fixture.Counting.SlotReads, Is.Zero, "priority reads that reached the flat persistence");

        Write(fixture.Priority, batch =>
        {
            batch.SetStorage(PriorityA, 1, 0x11);
            batch.SetStorage(PriorityA, 2, null);
            batch.SetStorage(PriorityA, 4, 0x44);
        });
        using IPersistence.IPersistenceReader after = fixture.Priority.CreateReader();

        AssertSlots(after, (PriorityA, 1, 0x11), (PriorityA, 2, null), (PriorityA, 4, 0x44), (PriorityB, 1, 0x0b), (Other, 1, 5));
        AssertSlots(before, (PriorityA, 1, 1), (PriorityA, 2, UInt256.Zero), (PriorityA, 4, null), (Other, 1, 5));
        Assert.That(fixture.Counting.SlotReads, Is.EqualTo(2), "only the reads of the other contract reach the flat persistence");
    }

    [Test]
    public void Mirrors_every_slot_change_of_the_flat_persistence([ValueSource(nameof(Changes))] MirrorCase change, [Values] FlatPriorityReadBackend backend)
    {
        using Fixture fixture = new(backend, preimage: false, cacheTrie: false, batch =>
        {
            for (int slot = 1; slot <= 8; slot++)
            {
                batch.SetStorage(PriorityA, (UInt256)slot, (UInt256)(0x100 + slot));
                batch.SetStorage(PriorityB, (UInt256)slot, (UInt256)(0x200 + slot));
                batch.SetStorage(Other, (UInt256)slot, (UInt256)(0x300 + slot));
            }
        });

        change.Apply(fixture.Priority);

        using IPersistence.IPersistenceReader expected = fixture.Flat.CreateReader();
        using IPersistence.IPersistenceReader actual = fixture.Priority.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.CurrentState, Is.EqualTo(expected.CurrentState), "state");
            foreach (Address address in new[] { PriorityA, PriorityB, Other })
            {
                for (int slot = 0; slot <= 10; slot++)
                {
                    Assert.That(Read(actual, address, (UInt256)slot), Is.EqualTo(Read(expected, address, (UInt256)slot)), $"{address} slot {slot}");
                }
            }
        }
    }

    [Test]
    public void Serves_cached_storage_trie_nodes_from_the_store_and_keeps_reader_snapshots([Values(FlatPriorityReadBackend.Lmdb, FlatPriorityReadBackend.RocksDb)] FlatPriorityReadBackend backend)
    {
        HashSet<TreePath> seeded = [];
        Hash256 root = Keccak.EmptyTreeHash;
        using Fixture fixture = new(backend, preimage: false, cacheTrie: true, batch =>
        {
            root = StorageTrieWriter.Write(null, batch, PriorityA, Keccak.EmptyTreeHash, Slots(1, 40, 0x100), seeded);
            StorageTrieWriter.Write(null, batch, Other, Keccak.EmptyTreeHash, Slots(1, 10, 0x300), new HashSet<TreePath>());
        });
        int loadReads = fixture.Counting.StorageNodeReads;
        Dictionary<TreePath, byte[]?> seededNodes = ReadNodes(fixture.Flat, PriorityA, seeded.Append(TreePath.FromNibble([0x0, 0x0, 0x0])));

        using IPersistence.IPersistenceReader before = fixture.Priority.CreateReader();
        AssertNodes(before, PriorityA, seededNodes);
        Assert.That(fixture.Counting.StorageNodeReads, Is.EqualTo(loadReads), "priority trie reads that reached the flat persistence");

        HashSet<TreePath> updated = [];
        Write(fixture.Priority, batch => StorageTrieWriter.Write(before, batch, PriorityA, root, Slots(30, 60, 0x200), updated));
        using IPersistence.IPersistenceReader after = fixture.Priority.CreateReader();

        AssertNodes(after, PriorityA, ReadNodes(fixture.Flat, PriorityA, seeded.Union(updated)));
        AssertNodes(before, PriorityA, seeded.Union(updated).ToDictionary(path => path, path => seededNodes.GetValueOrDefault(path)));
        byte[]? otherRoot = before.TryLoadStorageRlp(Other.ToAccountPath.ToCommitment(), TreePath.Empty, ReadFlags.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherRoot, Is.Not.Null, "the root of the other contract");
            Assert.That(fixture.Counting.StorageNodeReads, Is.EqualTo(loadReads + 1), "only the read of the other contract reaches the flat persistence");
        }
    }

    [Test]
    public void Mirrors_every_trie_node_change_of_the_flat_persistence([ValueSource(nameof(TrieChanges))] MirrorCase change,
        [Values(FlatPriorityReadBackend.Lmdb, FlatPriorityReadBackend.RocksDb)] FlatPriorityReadBackend backend)
    {
        Dictionary<Address, HashSet<TreePath>> paths = new() { [PriorityA] = [], [PriorityB] = [], [Other] = [] };
        using Fixture fixture = new(backend, preimage: false, cacheTrie: true, batch =>
        {
            foreach ((Address address, HashSet<TreePath> written) in paths) StorageTrieWriter.Write(null, batch, address, Keccak.EmptyTreeHash, Slots(1, 40, 0x100), written);
        });

        change.Apply(fixture.Priority);

        using IPersistence.IPersistenceReader actual = fixture.Priority.CreateReader();
        using (IPersistence.IPersistenceReader expected = fixture.Flat.CreateReader()) Assert.That(actual.CurrentState, Is.EqualTo(expected.CurrentState), "state");
        foreach ((Address address, HashSet<TreePath> written) in paths) AssertNodes(actual, address, ReadNodes(fixture.Flat, address, written));
    }

    public sealed record MirrorCase(string Name, Action<IPersistence> Apply)
    {
        public override string ToString() => Name;
    }

    private static IEnumerable<(UInt256 Slot, UInt256 Value)> Slots(int first, int last, int valueOffset) =>
        Enumerable.Range(first, last - first + 1).Select(slot => ((UInt256)slot, (UInt256)(valueOffset + slot)));

    /// <summary>Reads the storage trie node of <paramref name="address"/> at each path from a fresh reader of <paramref name="persistence"/>.</summary>
    /// <remarks>
    /// A fresh reader, rather than one opened before a write, as <see cref="SnapshotableMemDb"/> may prune what an older
    /// snapshot needs once another snapshot of the same version is disposed.
    /// </remarks>
    private static Dictionary<TreePath, byte[]?> ReadNodes(IPersistence persistence, Address address, IEnumerable<TreePath> paths)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        Hash256 account = address.ToAccountPath.ToCommitment();
        return paths.Distinct().ToDictionary(path => path, path => reader.TryLoadStorageRlp(account, path, ReadFlags.None));
    }

    /// <summary>Asserts the reader returns the expected storage trie node, or none, at each path, through both read overloads.</summary>
    private static void AssertNodes(IPersistence.IPersistenceReader reader, Address address, IReadOnlyDictionary<TreePath, byte[]?> expected)
    {
        Hash256 account = address.ToAccountPath.ToCommitment();
        using (Assert.EnterMultipleScope())
        {
            foreach ((TreePath path, byte[]? rlp) in expected)
            {
                Assert.That(reader.TryLoadStorageRlp(account, path, ReadFlags.None), Is.EqualTo(rlp), $"{address} node {path}");

                StrongBox<byte[]?> passed = new();
                bool found = reader.TryLoadStorageRlp(account, path, ReadFlags.None, static (span, box) => box.Value = span.ToArray(), passed);
                Assert.That(found, Is.EqualTo(rlp is not null), $"{address} node {path} found");
                Assert.That(passed.Value, Is.EqualTo(rlp), $"{address} node {path} passed");
            }
        }
    }

    private static ValueHash256 SlotHash(UInt256 slot)
    {
        ValueHash256 hash = default;
        StorageTree.ComputeKeyWithLookup(slot, ref hash);
        return hash;
    }

    private static void Write(IPersistence persistence, Action<IPersistence.IWriteBatch> write)
    {
        StateId from;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader()) from = reader.CurrentState;

        ulong block = from == StateId.PreGenesis ? 0 : from.BlockNumber + 1;
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(from, new StateId(block, Keccak.Compute(block.ToString())));
        write(batch);
    }

    private static UInt256? Read(IPersistence.IPersistenceReader reader, Address address, UInt256 slot)
    {
        UInt256 value = default;
        return reader.TryGetSlot(address, slot, ref value) ? value : null;
    }

    private static void AssertSlots(IPersistence.IPersistenceReader reader, params (Address Address, int Slot, UInt256? Value)[] expected)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((Address address, int slot, UInt256? value) in expected)
            {
                Assert.That(Read(reader, address, (UInt256)slot), Is.EqualTo(value), $"{address} slot {slot}");
            }
        }
    }

    /// <summary>A flat persistence seeded with one batch, behind a read counter and the priority read decorator.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempPath _directory = TempPath.GetTempDirectory();
        private readonly SnapshotableMemColumnsDb<FlatDbColumns> _db = new();
        private readonly IPrioritySlotStore _store;

        public Fixture(FlatPriorityReadBackend backend, bool preimage, bool cacheTrie, Action<IPersistence.IWriteBatch> seed)
        {
            Flat = preimage
                ? new PreimageRocksdbPersistence(_db, LimboLogs.Instance, FlatLayout.PreimageFlat, NullTrieNodeLog.Instance)
                : new RocksDbPersistence(_db, LimboLogs.Instance, NullTrieNodeLog.Instance);
            Write(Flat, seed);

            Counting = new ReadCountingPersistence(Flat);
            _store = TestPrioritySlotStores.Create(backend, _directory.Path);
            Priority = new FlatPriorityReadPersistence(Counting, _store, new FlatPriorityReadConfig { Addresses = new() { [PriorityA.ToString()] = "A", [PriorityB.ToString()] = "B" }, CacheTrie = cacheTrie }, LimboLogs.Instance);
        }

        /// <summary>The flat persistence on its own, which the decorator must agree with.</summary>
        public IPersistence Flat { get; }

        public ReadCountingPersistence Counting { get; }

        public FlatPriorityReadPersistence Priority { get; }

        public void Dispose()
        {
            _store.Dispose();
            _db.Dispose();
            _directory.Dispose();
        }
    }
}
