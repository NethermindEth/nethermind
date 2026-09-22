// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtRocksDbPersistenceTests
{
    [Test]
    public void Iterator_disposes_underlying_enumerator_on_completion_early_exit_or_failure([Range(0, 2)] int exitMode)
    {
        IEnumerator<int> enumerator = Substitute.For<IEnumerator<int>>();
        enumerator.MoveNext().Returns(_ => true, _ => exitMode == 2 ? throw new InvalidDataException() : false);
        enumerator.Current.Returns(7);

        void Iterate()
        {
            using IPbtIterator<int> iterator = new PbtIterator<int>(enumerator);
            while (iterator.MoveNext())
            {
                Assert.That(iterator.Current, Is.EqualTo(7));
                if (exitMode == 1) break;
            }
        }

        if (exitMode == 2) Assert.Throws<InvalidDataException>(Iterate);
        else Iterate();
        enumerator.Received(1).Dispose();
    }

    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private static ReadOnlySpan<byte> ValidStateKey => "validState"u8;
    private static ReadOnlySpan<byte> NodeGroupKeyLayoutKey => "nodeGroupKeyLayout"u8;

    [TestCase(null, PbtNodeGroupKeyLayout.Padded, PbtNodeGroupKeyLayout.Variable, TestName = "Unstamped_epoch_20_store_is_padded")]
    [TestCase(new byte[] { 0 }, PbtNodeGroupKeyLayout.Padded, PbtNodeGroupKeyLayout.Variable, TestName = "Padded_stamp_rejects_variable")]
    [TestCase(new byte[] { 1 }, PbtNodeGroupKeyLayout.Variable, PbtNodeGroupKeyLayout.Padded, TestName = "Variable_stamp_rejects_padded")]
    [TestCase(new byte[] { 2 }, null, PbtNodeGroupKeyLayout.Padded, TestName = "Unknown_stamp_is_rejected")]
    [TestCase(new byte[] { 0, 0 }, null, PbtNodeGroupKeyLayout.Variable, TestName = "Malformed_stamp_is_rejected")]
    public void Node_group_key_layout_stamp_gates_the_configured_layout(byte[]? stamp, PbtNodeGroupKeyLayout? accepted, PbtNodeGroupKeyLayout rejected)
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        metadata[SchemaEpochKey] = Epoch(20);
        if (stamp is not null) metadata[NodeGroupKeyLayoutKey] = stamp;

        using (Assert.EnterMultipleScope())
        {
            if (accepted is not null)
                Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { NodeGroupKeyLayout = accepted.Value }), Throws.Nothing);
            Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { NodeGroupKeyLayout = rejected }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("layout"));
            Assert.That(metadata.Get(NodeGroupKeyLayoutKey), Is.EqualTo(stamp));
        }
    }

    [Test]
    public void Fresh_store_is_stamped_with_the_configured_layout([Values] PbtNodeGroupKeyLayout layout)
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig { NodeGroupKeyLayout = layout });
        PbtNodePath groupKey = new(Bytes.FromHexString("80"), 8);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), default, WriteFlags.None))
        {
            WriteGroup(batch, groupKey.AppendNib(1), BranchNode(1));
            batch.Commit();
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get(NodeGroupKeyLayoutKey), Is.EqualTo(new[] { (byte)layout }));
            Assert.That(db.GetColumnDb(PbtColumns.TopNodeGroups).Get(groupKey.ToStorageKey(PbtColumns.TopNodeGroups, layout)), Is.Not.Null);
            Assert.That(reader.EnumerateNodeGroupKeys().Drain(), Is.EqualTo(new[] { groupKey.ToPath<PbtStorageNodePath>() }));
            Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { NodeGroupKeyLayout = layout }), Throws.Nothing);
        }
    }

    [Test]
    public void Completed_epoch_20_store_reopens_and_serves_canonical_records()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtPath leaf = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        PbtNodePath path = new([], 0);
        ValueHash256 value = TestItem.KeccakA.ValueHash256;
        byte[] node = PbtNodeCodec.EncodeLeaf(leaf);
        StateId state = new(7, TestItem.KeccakB.ValueHash256);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, state, value, WriteFlags.None))
        {
            batch.SetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), new Account(7, 9, TestItem.KeccakC, TestItem.KeccakD));
            WriteGroup(batch, path, node);
            batch.Commit();
        }

        PbtRocksDbPersistence reopened = new(db, new PbtConfig());
        using IPbtPersistence.IReader reader = reopened.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(state));
            Assert.That(reader.CurrentRoot, Is.EqualTo(value));
            Assert.That(reader.GetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA)), Is.EqualTo(new Account(7, 9, TestItem.KeccakC, TestItem.KeccakD)));
            Assert.That(ReadNode(reader, path), Is.EqualTo(node));
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get(ValidStateKey), Is.EqualTo(new byte[] { 1 }));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Old_epoch_rejection_does_not_mutate_any_column(bool importEnabled)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
            db.GetColumnDb(column)[new byte[] { 1 }] = [2];
        db.GetColumnDb(PbtColumns.Metadata)[SchemaEpochKey] = Epoch(15);
        Dictionary<PbtColumns, KeyValuePair<byte[], byte[]>[]> before = [];
        foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
            before[column] = db.GetColumnDb(column).GetAll(ordered: true).ToArray();

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = importEnabled }),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("re-import"));

        using (Assert.EnterMultipleScope())
        {
            foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
            {
                KeyValuePair<byte[], byte[]>[] after = db.GetColumnDb(column).GetAll(ordered: true).ToArray();
                Assert.That(after.Select(entry => entry.Key), Is.EqualTo(before[column].Select(entry => entry.Key)), $"{column} keys");
                Assert.That(after.Select(entry => entry.Value), Is.EqualTo(before[column].Select(entry => entry.Value)), $"{column} values");
            }
        }
    }

    [TestCase(0)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(256)]
    public void Clear_storage_precedes_staged_runs_and_preserves_other_addresses(int slotNumber)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        PbtStorageTreeKey persistedKey = PbtStateKey.Storage(TestItem.AddressA, (UInt256)(uint)slotNumber);
        PbtStorageTreeKey stagedKey = PbtStateKey.Storage(TestItem.AddressA, (UInt256)(uint)(slotNumber + SlotRun.Width));
        PbtStorageTreeKey otherAddressKey = PbtStateKey.Storage(TestItem.AddressB, (UInt256)(uint)slotNumber);
        EvmWord original = EvmWordSlot.FromStripped(Bytes.FromHexString("0x1234"));
        EvmWord replacement = EvmWordSlot.FromStripped(Bytes.FromHexString("0x5678"));
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            batch.SetSlot(persistedKey, original);
            batch.SetSlot(otherAddressKey, original);
            batch.Commit();
        }
        using IPbtPersistence.IReader olderReader = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            batch.ClearStorage(addressHash);
            batch.SetSlot(persistedKey, replacement);
            batch.SetSlot(stagedKey, replacement);
            batch.Commit();
        }

        using IPbtPersistence.IReader reader = new PbtRocksDbPersistence(db, new PbtConfig()).CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetSlot(persistedKey), Is.EqualTo(replacement));
            Assert.That(reader.GetSlot(stagedKey), Is.EqualTo(replacement));
            Assert.That(reader.GetSlot(otherAddressKey), Is.EqualTo(original));
            Assert.That(olderReader.GetSlot(persistedKey), Is.EqualTo(original));
            Assert.That(reader.EnumerateStorage().Drain(), Has.Exactly(3).Items);
            Assert.That(reader.EnumerateStorage(addressHash).Drain().Select(entry => entry.Value), Is.All.EqualTo(replacement));
        }
    }

    [Test]
    public void Slots_persist_as_whole_runs_that_are_replaced_and_deleted_wholesale()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        EvmWord value = EvmWordSlot.FromStripped(Bytes.FromHexString("0x1234"));
        static PbtStorageTreeKey Key(uint slot) => PbtStateKey.Storage(TestItem.AddressA, slot);
        static byte[] Persisted(in PbtStorageTreeKey key) => PbtStorageKeyLayout.Encode(key, new byte[PbtStorageTreeKey.MaxLength]).ToArray();
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        StateId third = new(3, TestItem.KeccakC.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            foreach (uint start in new[] { 0u, 16u })
            {
                ISlotRun run = SlotRun.Empty;
                for (uint slot = start; slot <= Math.Min(start + 15, 20); slot++)
                {
                    ISlotRun previous = run;
                    run = run.With(SlotRun.IndexOf(Key(slot)), value);
                    SlotRun.Return(previous);
                }
                batch.SetSlotRun(SlotRun.RunKey(Key(start)), run);
                SlotRun.Return(run);
            }
            batch.Commit();
        }
        byte[][] populatedRows = db.GetColumnDb(PbtColumns.Storages).GetAllKeys().ToArray();
        using IPbtPersistence.IReader populated = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            batch.SetSlot(Key(3), value);
            batch.Commit();
        }
        using IPbtPersistence.IReader replaced = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(second, third, default, WriteFlags.None))
        {
            batch.SetSlotRun(SlotRun.RunKey(Key(3)), SlotRun.Empty);
            batch.Commit();
        }
        using IPbtPersistence.IReader deleted = persistence.CreateReader();
        ISlotRun tail = populated.GetSlotRun(SlotRun.RunKey(Key(16)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(populatedRows, Is.EquivalentTo(new[] { Persisted(SlotRun.RunKey(Key(0))), Persisted(SlotRun.RunKey(Key(16))) }));
            Assert.That(Enumerable.Range(0, 22).Select(slot => populated.GetSlot(Key((uint)slot))), Is.EqualTo(Enumerable.Range(0, 22).Select(slot => slot <= 20 ? value : default)));
            Assert.That(populated.EnumerateStorage(addressHash).Drain().Select(slot => slot.Key), Is.EqualTo(Enumerable.Range(0, 21).Select(slot => Key((uint)slot))));
            Assert.That(tail.Mask, Is.EqualTo(0x1F));
            Assert.That(populated.GetSlotRun(SlotRun.RunKey(Key(32))), Is.SameAs(SlotRun.Empty));
            Assert.That(() => populated.GetSlotRun(Key(3)), Throws.ArgumentException);
            Assert.That(new[] { replaced.GetSlot(Key(0)), replaced.GetSlot(Key(3)), replaced.GetSlot(Key(16)) }, Is.EqualTo(new[] { default, value, value }));
            Assert.That(replaced.EnumerateStorage(addressHash).Drain(), Has.Count.EqualTo(6));
            Assert.That(deleted.GetSlot(Key(3)), Is.EqualTo(default(EvmWord)));
            Assert.That(db.GetColumnDb(PbtColumns.Storages).GetAllKeys(), Is.EquivalentTo(new[] { Persisted(SlotRun.RunKey(Key(16))) }));
        }
        SlotRun.Return(tail);
    }

    [Test]
    public void Storage_rows_are_keyed_address_first_so_one_account_is_one_range([Values(1u, 64u)] uint otherAddressSlot)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        PbtStorageTreeKey headerKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtStorageTreeKey overflowKey = PbtStateKey.Storage(TestItem.AddressA, PbtKeyDerivation.HeaderStorageOffset);
        PbtStorageTreeKey otherAddressKey = PbtStateKey.Storage(TestItem.AddressB, otherAddressSlot);
        EvmWord value = EvmWordSlot.FromStripped(Bytes.FromHexString("0x1234"));
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            batch.SetSlot(headerKey, value);
            batch.SetSlot(overflowKey, value);
            batch.SetSlot(otherAddressKey, value);
            batch.Commit();
        }
        using IPbtPersistence.IReader populated = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            batch.ClearStorage(addressHash);
            batch.Commit();
        }
        using IPbtPersistence.IReader cleared = persistence.CreateReader();

        static byte[] Persisted(in PbtStorageTreeKey key) => PbtStorageKeyLayout.Encode(key, new byte[PbtStorageTreeKey.MaxLength]).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Persisted(headerKey), Is.EqualTo(Bytes.Concat(addressHash.Bytes, [Eip8297KeyDerivation.AccountZone], headerKey.Bytes[33..])));
            Assert.That(Persisted(overflowKey), Is.EqualTo(Bytes.Concat(addressHash.Bytes, [Eip8297KeyDerivation.StorageZone], overflowKey.Bytes[33..])));
            Assert.That(PbtStorageKeyLayout.Decode(Persisted(headerKey)), Is.EqualTo(headerKey));
            Assert.That(PbtStorageKeyLayout.Decode(Persisted(overflowKey)), Is.EqualTo(overflowKey));
            Assert.That(populated.EnumerateStorage(addressHash).Drain().Select(slot => slot.Key), Is.EqualTo(new[] { headerKey, overflowKey }));
            Assert.That(populated.EnumerateStorage().Drain().Select(slot => slot.Key), Is.EquivalentTo(new[] { headerKey, overflowKey, otherAddressKey }));
            Assert.That(cleared.EnumerateStorage().Drain().Select(slot => slot.Key), Is.EqualTo(new[] { otherAddressKey }));
            Assert.That(db.GetColumnDb(PbtColumns.Storages).GetAllKeys(), Is.EquivalentTo(new[] { Persisted(SlotRun.RunKey(otherAddressKey)) }));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Typed_tombstones_and_whole_code_are_published_atomically(bool commit)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        PbtStorageTreeKey storageKey = PbtStateKey.Storage(TestItem.AddressA, 0);
        CodeInfo code = new(Bytes.FromHexString("0x6001600255"));
        ValueHash256 codeHash = Keccak.Compute(code.CodeSpan).ValueHash256;
        EvmWord slot = EvmWordSlot.FromStripped(Bytes.FromHexString("0xabcd"));
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            batch.SetAccount(addressHash, Account.TotallyEmpty);
            batch.SetSlot(storageKey, slot);
            batch.SetCode(codeHash, code);
            batch.Commit();
        }
        Assert.That(() => persistence.CreateWriteBatch(StateId.PreGenesis, second, default, WriteFlags.None), Throws.InvalidOperationException);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            batch.SetAccount(addressHash, null);
            batch.SetSlot(storageKey, default);
            if (commit) batch.Commit();
        }

        using IPbtPersistence.IReader reader = new PbtRocksDbPersistence(db, new PbtConfig()).CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(commit ? second : first));
            Assert.That(reader.GetAccount(addressHash), Is.EqualTo(commit ? null : Account.TotallyEmpty));
            Assert.That(reader.GetSlot(storageKey), Is.EqualTo(commit ? default : slot));
            Assert.That(reader.EnumerateAccounts().Drain().Count, Is.EqualTo(commit ? 0 : 1));
            Assert.That(reader.EnumerateStorage().Drain().Count, Is.EqualTo(commit ? 0 : 1));
            Assert.That(reader.GetCode(codeHash), Is.EqualTo(code));
            Assert.That(reader.GetCode(TestItem.KeccakC.ValueHash256), Is.Null);
            Assert.That(db.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
        }
    }

    [Test]
    public void Whole_group_replacements_remove_omitted_nodes_and_null_deletes_the_group()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtNodePath firstPath = new([0], 1);
        PbtNodePath secondPath = new([0], 2);
        byte[] firstNode = BranchNode(1);
        byte[] secondNode = BranchNode(2);
        StateId firstState = new(1, TestItem.KeccakA.ValueHash256);
        StateId secondState = new(2, TestItem.KeccakB.ValueHash256);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, firstState, default, WriteFlags.None))
        {
            using RefCountingMemory group = EncodeGroup(null, new(firstPath.ToPath<PbtStorageNodePath>(), firstNode), new(secondPath.ToPath<PbtStorageNodePath>(), secondNode));
            batch.SetNodeGroup(PbtFourLevelGroupGeometry.Locate(firstPath).GroupKey, group);
            batch.Commit();
        }

        IDb physicalGroups = db.GetColumnDb(PbtColumns.Metadata);
        Assert.That(physicalGroups.Get("rootNodeGroup"u8), Is.Not.Null);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(firstState, secondState, default, WriteFlags.None))
        {
            WriteGroup(batch, secondPath, secondNode);
            batch.Commit();
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            PbtStorageNodePath[] groupKeys = [.. reader.EnumerateNodeGroupKeys().Drain()];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadNode(reader, firstPath), Is.Null);
                Assert.That(ReadNode(reader, secondPath), Is.EqualTo(secondNode));
                Assert.That(groupKeys, Is.EqualTo(new[] { PbtFourLevelGroupGeometry.Locate(secondPath).GroupKey.ToPath<PbtStorageNodePath>() }));
                Assert.That(physicalGroups.Get("rootNodeGroup"u8), Is.Not.Null);
            }
        }

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(secondState, new StateId(3, default), default, WriteFlags.None))
        {
            batch.SetNodeGroup(PbtFourLevelGroupGeometry.Locate(secondPath).GroupKey, null);
            batch.Commit();
        }
        Assert.That(physicalGroups.Get("rootNodeGroup"u8), Is.Null);
    }

    [Test]
    public void Grouped_writes_use_the_compact_footer_and_release_rented_payloads()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        TrackingMemoryProvider memoryProvider = new();
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtNodePath path = new([0], 1);
        byte[] node = BranchNode(1);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None))
        {
            WriteGroup(batch, path, node, memoryProvider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            batch.Commit();
        }

        byte[] payload = db.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8)!;
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(PbtFourLevelGroupGeometry.Locate(path).GroupKey, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Length, Is.EqualTo(node.Length + PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.GetTrailerLength(1, 0)));
            Assert.That(reader.GetNode(PbtFourLevelGroupGeometry.Locate(path).Position).ToArray(), Is.EqualTo(node));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }
    }

    [Test]
    public void Failed_group_write_releases_rented_payloads_without_committing()
    {
        SnapshotableMemColumnsDb<PbtColumns> inner = new("pbt");
        FailNextCommitColumnsDb db = new(inner);
        TrackingMemoryProvider memoryProvider = new();
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        db.FailNextCommit = true;
        IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None);
        WriteGroup(batch, new PbtNodePath([], 0), BranchNode(1), memoryProvider);

        Assert.That(() => batch.Commit(), Throws.TypeOf<IOException>());
        batch.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8), Is.Null);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Group_writes_copy_borrowed_payloads_and_commit_or_discard(bool commit)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtNodePath[] paths = [new(Bytes.FromHexString("8000"), 9), new([], 0), new(Bytes.FromHexString("00"), 5),
            new(Bytes.FromHexString("0100"), 9), new(Bytes.FromHexString("ff00"), 9), new(Bytes.FromHexString("000000"), 17)];
        using IPbtPersistence.IReader olderReader = persistence.CreateReader();
        TrackingMemoryProvider memoryProvider = new();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None))
        {
            foreach (PbtNodePath path in paths) WriteGroup(batch, path, BranchNode(1), memoryProvider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            if (commit) batch.Commit();
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        PbtStorageNodePath[] expected = commit ? [new([], 0), new(Bytes.FromHexString("00"), 4), new(Bytes.FromHexString("01"), 8),
            new(Bytes.FromHexString("80"), 8), new(Bytes.FromHexString("ff"), 8), new(Bytes.FromHexString("0000"), 16)] : [];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(olderReader.EnumerateNodeGroupKeys().Drain(), Is.Empty);
            Assert.That(olderReader.CurrentState, Is.EqualTo(StateId.PreGenesis));
            foreach (PbtNodePath path in paths) Assert.That(ReadNode(olderReader, path), Is.Null);
            Assert.That(reader.EnumerateNodeGroupKeys().Drain(), Is.EquivalentTo(expected));
            Assert.That(reader.CurrentState, Is.EqualTo(commit ? new StateId(1, default) : StateId.PreGenesis));
            foreach (PbtNodePath path in paths)
                Assert.That(ReadNode(reader, path), commit ? Is.EqualTo(BranchNode(1)) : Is.Null);
        }
    }

    [Test]
    public void Incremental_flush_reopens_with_partial_tail_and_latest_values()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        using MemDb metadata = new();
        PbtConfig config = new() { CompactSize = 2, CompactionOffset = 0 };
        DbConfig dbConfig = new();
        PbtRocksDbConfigAdjuster adjuster = new(Substitute.For<IRocksDbConfigFactory>(), dbConfig, config, Substitute.For<IDisposableStack>(), LimboLogs.Instance);
        PbtResourcePool pool = new(config, PooledRefCountingMemoryProvider.Instance);
        PbtSnapshotRepository repository = new(new MetricsConfig());
        PbtCompactionSchedule schedule = new(metadata, config, LimboLogs.Instance);
        PbtSnapshotCompactor compactor = new(pool, schedule, repository, config);
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < 32; index++)
        {
            byte[] key = [(byte)(index << 3)];
            byte[] value = Value((byte)(index + 1));
            changes.Add((key, value));
            oracle.Insert(key, value);
        }
        tree.ApplyBatch(changes);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
        StateId last = StateId.PreGenesis;
        try
        {
            using (ColumnsDb<PbtColumns> db = new(dbPath.Path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig,
                adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>()))
            {
                PbtRocksDbPersistence persistence = new(db, config);
                PbtPersistenceCoordinator coordinator = new(config, new PbtTestContext.TestFinalizedStateProvider(), persistence,
                    repository, schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
                for (ulong number = 0; number <= 5; number++)
                {
                    StateId next = new(number, TestItem.KeccakA.ValueHash256);
                    PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
                    content.Accounts[addressHash] = new Account(number, number + 10);
                    foreach (PbtPhysicalPayload physical in tree.PhysicalPayloads)
                    {
                        using RefCountingMemory payload = RefCountingMemory.Wrapping(physical.Payload.ToArray());
                        content.SetNodeGroup(physical.Key, payload);
                    }
                    repository.TryAdd(new PbtSnapshot(last, next, tree.RootHash, content, pool, PbtResourcePool.Usage.MainBlockProcessing));
                    compactor.DoCompactSnapshot(next);
                    last = next;
                }
                coordinator.FlushToPersistence();
                Assert.That(repository.Count, Is.Zero);
                Assert.That(coordinator.CheckPersistence(new StateId(2, TestItem.KeccakA.ValueHash256)), Is.False, "queued IDs behind persistence are harmless");
            }

            using ColumnsDb<PbtColumns> reopenedDb = new(dbPath.Path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig,
                adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>());
            using IPbtPersistence.IReader reader = new PbtRocksDbPersistence(reopenedDb, config).CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(last));
                Assert.That(reader.CurrentRoot, Is.EqualTo(tree.RootHash));
                Assert.That(reader.GetAccount(addressHash), Is.EqualTo(new Account(5, 15)));
            }
            List<PbtPhysicalPayload> persisted = [];
            using IPbtIterator<PbtStorageNodePath> groupKeys = reader.EnumerateNodeGroupKeys();
            while (groupKeys.MoveNext())
            {
                PbtStorageNodePath groupKey = groupKeys.Current;
                using RefCountingMemory payload = reader.GetNodeGroup(groupKey)!;
                PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan());
                persisted.Add(new PbtPhysicalPayload(groupKey, payload.GetSpan()));
            }
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(persisted);
            Assert.That(reopened.EnumerateRecords().Count, Is.EqualTo(tree.Nodes.Count));
            using PbtWriteBatchBuilder<PbtStorageTreeKey> mutations = new(0);
            byte[] deletedKey = Bytes.FromHexString("00");
            byte[] replacedKey = Bytes.FromHexString("08");
            mutations.Delete(new PbtStorageTreeKey(deletedKey));
            mutations.Set(new PbtStorageTreeKey(replacedKey), new ValueHash256(Value(99)));
            oracle.Delete(deletedKey);
            oracle.Insert(replacedKey, Value(99));
            ValueHash256 updatedRoot = TrieUpdater.UpdateRoot(reopened, reader.CurrentRoot, mutations.Build());
            Assert.That(updatedRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(reopened.EnumerateRecords(), Has.Count.EqualTo(30).And.All.Matches<PbtNodeRecord>(record => record.Encoding.Span[0] == 1), "the promoted leaf leaves 30 branches, all with inline leaves");
        }
        finally
        {
            repository.RemoveStatesUntil(ulong.MaxValue);
        }
    }

    [TestCase("00", 1)]
    [TestCase("0000", 9)]
    [TestCase("0100", 9)]
    [TestCase("ff00", 9)]
    public void Node_group_lease_survives_reader_and_persistence_changes_until_disposed(string prefix, int depth)
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        PbtRocksDbConfigAdjuster adjuster = new(Substitute.For<IRocksDbConfigFactory>(), dbConfig, new PbtConfig(), Substitute.For<IDisposableStack>(), LimboLogs.Instance);
        ColumnsDb<PbtColumns> db = new(dbPath.Path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig,
            adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>());
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtNodePath path = new(Bytes.FromHexString(prefix), depth);
        PbtNodePath groupKey = PbtFourLevelGroupGeometry.Locate(path).GroupKey;
        byte[] originalNode = BranchNode(1);
        byte[] replacementNode = BranchNode(2);
        RefCountingMemory payload;

        try
        {
            using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
                StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None))
            {
                WriteGroup(batch, path, originalNode);
                batch.Commit();
            }

            using (IPbtPersistence.IReader reader = persistence.CreateReader())
            {
                payload = reader.GetNodeGroup(groupKey)!;
                Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan()).GetNode(PbtFourLevelGroupGeometry.PositionOf(path)).ToArray(),
                    Is.EqualTo(originalNode));
            }

            using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
                new StateId(1, default), new StateId(2, default), default, WriteFlags.None))
            {
                WriteGroup(batch, path, replacementNode);
                batch.Commit();
            }

            Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan()).GetNode(PbtFourLevelGroupGeometry.PositionOf(path)).ToArray(),
                Is.EqualTo(originalNode));
            ((IDisposable)payload).Dispose();
        }
        finally
        {
            db.Dispose();
        }
    }

    [Test]
    public void Fresh_store_is_versioned_but_remains_unpublished_until_the_first_commit()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");

        PbtRocksDbPersistence persistence = new(db, new PbtConfig());

        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.Get(SchemaEpochKey), Is.EqualTo(Epoch(20)));
            Assert.That(metadata.Get(CurrentStateKey), Is.Null);
            Assert.That(metadata.Get(ValidStateKey), Is.Null);
        }
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
    }

    [Test]
    public void Staging_never_publishes_current_state_or_validity()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig { ImportFromPreimageFlat = true });
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateStagingWriteBatch(WriteFlags.None))
        {
            batch.SetAccount(addressHash, Account.TotallyEmpty);
            batch.Commit();
        }

        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.GetColumnDb(PbtColumns.Accounts).Get(addressHash.Bytes), Is.Not.Null);
            Assert.That(metadata.Get(CurrentStateKey), Is.Null);
            Assert.That(metadata.Get(ValidStateKey), Is.Null);
        }
        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig()),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("interrupted initialization"));
        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = true }), Throws.Nothing);
    }

    [Test]
    public void Import_mode_does_not_clear_a_valid_pre_genesis_store()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig { ImportFromPreimageFlat = true });
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis,
            StateId.PreGenesis,
            default,
            WriteFlags.None))
        {
            batch.SetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), Account.TotallyEmpty);
            batch.Commit();
        }

        Assert.That(persistence.IsValid, Is.True);
        Assert.That(new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = true }).IsValid, Is.True);
    }

    [Test]
    public void Failed_final_commit_does_not_publish_state_or_validity()
    {
        SnapshotableMemColumnsDb<PbtColumns> inner = new("pbt");
        FailNextCommitColumnsDb db = new(inner);
        PbtRocksDbPersistence persistence = new(db, new PbtConfig { ImportFromPreimageFlat = true });
        ValueHash256 stagedAddressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        using (IPbtPersistence.IWriteBatch staging = persistence.CreateStagingWriteBatch(WriteFlags.None))
        {
            staging.SetAccount(stagedAddressHash, Account.TotallyEmpty);
            staging.Commit();
        }

        db.FailNextCommit = true;
        IPbtPersistence.IWriteBatch final = persistence.CreateWriteBatch(
            StateId.PreGenesis,
            new StateId(7, TestItem.KeccakB.ValueHash256),
            TestItem.KeccakA.ValueHash256,
            WriteFlags.None);
        PbtTreeKey nodeKey = new([0x80]);
        WriteGroup(final, new PbtNodePath([], 0), PbtNodeCodec.EncodeLeaf(nodeKey));

        Assert.That(() => final.Commit(), Throws.TypeOf<IOException>());
        final.Dispose();
        IDb metadata = inner.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.GetColumnDb(PbtColumns.Accounts).Get(stagedAddressHash.Bytes), Is.Not.Null);
            Assert.That(inner.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8), Is.Null);
            Assert.That(metadata.Get(CurrentStateKey), Is.Null);
            Assert.That(metadata.Get(ValidStateKey), Is.Null);
            Assert.That(() => new PbtRocksDbPersistence(inner, new PbtConfig()),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("interrupted initialization"));
            Assert.That(() => new PbtRocksDbPersistence(inner, new PbtConfig { ImportFromPreimageFlat = true }), Throws.Nothing);
        }
    }

    private static IEnumerable<TestCaseData> InvalidMetadataCases()
    {
        yield return new TestCaseData(Epoch(7), null, null, false).SetName("Rejects_epoch_7");
        yield return new TestCaseData(Epoch(8), null, null, false).SetName("Rejects_epoch_8");
        yield return new TestCaseData(Epoch(9), CurrentState(), new byte[] { 1 }, false).SetName("Rejects_epoch_9");
        yield return new TestCaseData(Epoch(18), CurrentState(), new byte[] { 1 }, false).SetName("Rejects_epoch_18");
        yield return new TestCaseData(new byte[] { 9 }, null, null, false).SetName("Rejects_malformed_epoch");
        yield return new TestCaseData(Epoch(20), new byte[] { 0 }, null, false).SetName("Rejects_malformed_current_state");
        yield return new TestCaseData(Epoch(20), null, Array.Empty<byte>(), false).SetName("Rejects_empty_validity");
        yield return new TestCaseData(Epoch(20), null, new byte[] { 2 }, false).SetName("Rejects_unknown_validity");
        yield return new TestCaseData(Epoch(20), null, new byte[] { 1 }, false).SetName("Rejects_validity_without_current_state");
        yield return new TestCaseData(Epoch(20), CurrentState(), null, false).SetName("Rejects_current_state_without_validity");
        yield return new TestCaseData(null, CurrentState(), null, false).SetName("Rejects_unstamped_current_state");
        yield return new TestCaseData(null, null, null, true).SetName("Rejects_unstamped_populated_store");
    }

    [TestCaseSource(nameof(InvalidMetadataCases))]
    public void Invalid_or_inconsistent_metadata_is_rejected_before_node_column_access(
        byte[]? epoch,
        byte[]? currentState,
        byte[]? validity,
        bool populateLegacyColumn)
    {
        SnapshotableMemColumnsDb<PbtColumns> inner = new("pbt");
        IDb metadata = inner.GetColumnDb(PbtColumns.Metadata);
        if (epoch is not null) metadata[SchemaEpochKey] = epoch;
        if (currentState is not null) metadata[CurrentStateKey] = currentState;
        if (validity is not null) metadata[ValidStateKey] = validity;
        if (populateLegacyColumn) inner.GetColumnDb(PbtColumns.FullLeaves)[new byte[] { 1 }] = [2];
        ThrowOnNodeGroupsDb db = new(inner);

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig()), Throws.TypeOf<InvalidDataException>());
        Assert.That(db.NodeGroupsAccessed, Is.False);
    }

    [TestCase(PbtColumns.Accounts)]
    [TestCase(PbtColumns.Storages)]
    [TestCase(PbtColumns.Codes)]
    [TestCase(PbtColumns.FullLeaves)]
    [TestCase(PbtColumns.AccountNodeGroups)]
    [TestCase(PbtColumns.CodeNodeGroups)]
    [TestCase(PbtColumns.StorageNodeGroups)]
    [TestCase(PbtColumns.TopNodeGroups)]
    [TestCase(PbtColumns.AccountLeaves)]
    [TestCase(PbtColumns.CodeLeaves)]
    [TestCase(PbtColumns.StorageLeaves)]
    [TestCase(PbtColumns.AccountTrieNodes)]
    [TestCase(PbtColumns.CodeTrieNodes)]
    [TestCase(PbtColumns.StorageTrieNodes)]
    public void Unstamped_populated_canonical_or_reserved_column_is_rejected(PbtColumns column)
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        db.GetColumnDb(column)[new byte[] { 1 }] = [2];

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = true }),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("no schema epoch"));
    }

    private static IEnumerable<TestCaseData> PartitionCases()
    {
        yield return new TestCaseData(new PbtNodePath([], 0), PbtColumns.Metadata);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("00"), 5), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("f0"), 5), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("00000000"), 29), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("0000000000"), 33), PbtColumns.AccountNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("8000000000"), 33), PbtColumns.AccountNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("010000000000000000000000000000000000000000000000000000000000000000"), 261), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("01000000000000000000000000000000000000000000000000000000000000000000"), 265), PbtColumns.CodeNodeGroups);
        yield return new TestCaseData(new PbtStorageNodePath(Bytes.FromHexString("ff0000000000000000000000000000000000000000000000000000000000000000"), 261), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtStorageNodePath(Bytes.FromHexString("ff000000000000000000000000000000000000000000000000000000000000000000"), 265), PbtColumns.StorageNodeGroups);
        yield return new TestCaseData(new PbtStorageNodePath(Bytes.FromHexString("ff" + new string('0', 130)), 525), PbtColumns.StorageNodeGroups);
    }

    [TestCaseSource(nameof(PartitionCases))]
    public void Partition_groups_replace_and_delete_only_their_physical_record<TPath>(TPath path, PbtColumns column)
        where TPath : struct, IPbtNodePath<TPath>
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        TPath groupKey = PbtFourLevelGroupGeometry.Locate(path).GroupKey;
        byte[] physicalKey = groupKey.ToStorageKey(column, PbtNodeGroupKeyLayout.Padded);
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            WriteGroup(batch, path, BranchNode(1));
            batch.Commit();
        }
        using IPbtPersistence.IReader olderReader = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            WriteGroup(batch, path, BranchNode(2));
            batch.Commit();
        }
        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadNode(reader, path), Is.EqualTo(BranchNode(2)));
                Assert.That(ReadNode(olderReader, path), Is.EqualTo(BranchNode(1)));
                Assert.That(reader.EnumerateNodeGroupKeys().Drain(), Is.EqualTo(new[] { groupKey.ToPath<PbtStorageNodePath>() }));
                Assert.That(db.GetColumnDb(column).Get(physicalKey), Is.Not.Null);
                foreach (PbtColumns otherColumn in new[] { PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups })
                    if (otherColumn != column) Assert.That(db.GetColumnDb(otherColumn).GetAll(), Is.Empty, otherColumn.ToString());
                if (column != PbtColumns.Metadata) Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8), Is.Null);
            }
        }
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(second, new StateId(3, default), default, WriteFlags.None))
        {
            batch.SetNodeGroup(groupKey, null);
            batch.Commit();
        }
        using IPbtPersistence.IReader deletedReader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadNode(deletedReader, path), Is.Null);
            Assert.That(deletedReader.EnumerateNodeGroupKeys().Drain(), Is.Empty);
            Assert.That(db.GetColumnDb(column).Get(physicalKey), Is.Null);
            Assert.That(ReadNode(olderReader, path), Is.EqualTo(BranchNode(1)));
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get(SchemaEpochKey), Is.EqualTo(Epoch(20)));
        }
    }

    [Test]
    public void Unpublished_group_content_is_detected_in_every_location(
        [Values(PbtColumns.Metadata, PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups)] PbtColumns column,
        [Values] bool stamped)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        byte[] key = new PbtNodePath(Bytes.FromHexString("00"), 4).ToStorageKey(column, PbtNodeGroupKeyLayout.Padded);
        db.GetColumnDb(column).Set(key, Bytes.FromHexString("01"));
        if (stamped) db.GetColumnDb(PbtColumns.Metadata).Set(SchemaEpochKey, Epoch(20));

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig()),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains(stamped ? "interrupted initialization" : "no schema epoch"));
        if (stamped)
            Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = true }), Throws.Nothing);
        else
            Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = true }), Throws.TypeOf<InvalidDataException>());
        Assert.That(db.GetColumnDb(column).Get(key), Is.EqualTo(Bytes.FromHexString("01")));
    }

    private static RefCountingMemory EncodeGroup(IRefCountingMemoryProvider? memoryProvider, params PbtNodeRecord[] records)
    {
        BufferWriter writer = new(memoryProvider ?? PooledRefCountingMemoryProvider.Instance);
        try
        {
            PbtNodeGroupEncoder.Encode(ref writer, PbtFourLevelGroupGeometry.Locate(records[0].Path).GroupKey, records, default);
            return writer.Detach()!;
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteGroup<TPath>(IPbtPersistence.IWriteBatch batch, TPath path, byte[] node, IRefCountingMemoryProvider? memoryProvider = null)
        where TPath : struct, IPbtNodePath<TPath>
    {
        using RefCountingMemory payload = EncodeGroup(memoryProvider, new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), node));
        batch.SetNodeGroup(PbtFourLevelGroupGeometry.Locate(path).GroupKey, payload);
    }

    private static byte[]? ReadNode<TPath>(IPbtPersistence.IReader reader, TPath path)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? payload = reader.GetNodeGroup(location.GroupKey);
        if (payload is null) return null;
        PbtNodeGroupReader group = PbtStoreTestExtensions.ReadGroup(location.GroupKey, payload.GetSpan());
        return group.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding) ? encoding.ToArray() : null;
    }

    private static byte[] BranchNode(byte marker) => PbtNodeCodec.EncodeBranch(
        Bytes.FromHexString("80"), 1,
        new ValueHash256(Value(marker)),
        new ValueHash256(Value((byte)(marker + 1))));

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    private static byte[] Epoch(int epoch)
    {
        byte[] value = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, epoch);
        return value;
    }

    private static byte[] CurrentState()
    {
        byte[] value = new byte[sizeof(ulong) + 2 * ValueHash256.MemorySize];
        BinaryPrimitives.WriteUInt64BigEndian(value, 1);
        return value;
    }

    private sealed class FailNextCommitColumnsDb(IColumnsDb<PbtColumns> inner) : IColumnsDb<PbtColumns>
    {
        public bool FailNextCommit { get; set; }
        public IEnumerable<PbtColumns> ColumnKeys => inner.ColumnKeys;
        public long EstimatedCount => inner.EstimatedCount;
        public IDb GetColumnDb(PbtColumns key) => inner.GetColumnDb(key);
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() => inner.Dispose();

        public IColumnsWriteBatch<PbtColumns> StartWriteBatch()
        {
            IColumnsWriteBatch<PbtColumns> batch = inner.StartWriteBatch();
            if (!FailNextCommit) return batch;
            FailNextCommit = false;
            return new ThrowOnDisposeBatch(batch);
        }

        private sealed class ThrowOnDisposeBatch(IColumnsWriteBatch<PbtColumns> innerBatch) : IColumnsWriteBatch<PbtColumns>
        {
            public IWriteBatch GetColumnBatch(PbtColumns key) => innerBatch.GetColumnBatch(key);
            public void Clear() => innerBatch.Clear();
            public void Dispose()
            {
                innerBatch.Clear();
                innerBatch.Dispose();
                throw new IOException("commit failed");
            }
        }
    }

    private sealed class ThrowOnNodeGroupsDb(IColumnsDb<PbtColumns> inner) : IColumnsDb<PbtColumns>
    {
        public bool NodeGroupsAccessed { get; private set; }
        public IEnumerable<PbtColumns> ColumnKeys => inner.ColumnKeys;
        public long EstimatedCount => inner.EstimatedCount;

        public IDb GetColumnDb(PbtColumns key)
        {
            if (key is PbtColumns.AccountNodeGroups or PbtColumns.CodeNodeGroups or PbtColumns.StorageNodeGroups)
            {
                NodeGroupsAccessed = true;
                throw new AssertionException("Node groups were accessed before metadata rejection.");
            }
            return inner.GetColumnDb(key);
        }

        public IColumnsWriteBatch<PbtColumns> StartWriteBatch() => inner.StartWriteBatch();
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() => inner.Dispose();
    }
}
