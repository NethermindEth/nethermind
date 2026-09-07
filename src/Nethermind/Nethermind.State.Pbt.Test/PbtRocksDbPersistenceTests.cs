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
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtRocksDbPersistenceTests
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private static ReadOnlySpan<byte> ValidStateKey => "validState"u8;

    [Test]
    public void Completed_epoch_10_store_reopens_and_serves_canonical_records()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtFullKey leaf = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        PbtNodePath path = new([], 0);
        ValueHash256 value = TestItem.KeccakA.ValueHash256;
        byte[] node = PbtNodeCodec.Encode(new PbtLeafNode(leaf, value));
        StateId state = new(7, TestItem.KeccakB.ValueHash256);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, state, value, WriteFlags.None))
        {
            batch.SetLeaf(leaf, value);
            WriteGroup(batch, path, node);
            batch.Commit();
        }

        PbtRocksDbPersistence reopened = new(db, new PbtConfig());
        using IPbtPersistence.IReader reader = reopened.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(state));
            Assert.That(reader.CurrentRoot, Is.EqualTo(value));
            Assert.That(reader.GetLeaf(leaf), Is.EqualTo(value));
            Assert.That(ReadNode(reader, path), Is.EqualTo(node));
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get(ValidStateKey), Is.EqualTo(new byte[] { 1 }));
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
            using RefCountingMemory group = EncodeGroup(null, new(firstPath, firstNode), new(secondPath, secondNode));
            batch.SetNodeGroup(PbtFourLevelGroupGeometry.GroupKeyOf(firstPath), group);
            batch.Commit();
        }

        IDb physicalGroups = db.GetColumnDb(PbtColumns.NodeGroups);
        Assert.That(physicalGroups.GetAll().ToArray(), Has.Length.EqualTo(1));
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(firstState, secondState, default, WriteFlags.None))
        {
            WriteGroup(batch, secondPath, secondNode);
            batch.Commit();
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            PbtNodePath[] groupKeys = [.. reader.EnumerateNodeGroupKeys()];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadNode(reader, firstPath), Is.Null);
                Assert.That(ReadNode(reader, secondPath), Is.EqualTo(secondNode));
                Assert.That(groupKeys, Is.EqualTo(new[] { PbtFourLevelGroupGeometry.GroupKeyOf(secondPath) }));
                Assert.That(physicalGroups.GetAll().ToArray(), Has.Length.EqualTo(1));
            }
        }

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(secondState, new StateId(3, default), default, WriteFlags.None))
        {
            batch.SetNodeGroup(PbtFourLevelGroupGeometry.GroupKeyOf(secondPath), null);
            batch.Commit();
        }
        Assert.That(physicalGroups.GetAll(), Is.Empty);
    }

    [Test]
    public void Grouped_writes_use_the_fixed_footer_and_release_rented_payloads()
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

        byte[] payload = db.GetColumnDb(PbtColumns.NodeGroups).GetAll().Single().Value!;
        PbtNodeGroupReader reader = new(PbtFourLevelGroupGeometry.Locate(path).GroupKey, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Length, Is.EqualTo(node.Length + PbtNodeGroupCodec.TrailerLength));
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
            Assert.That(inner.GetColumnDb(PbtColumns.NodeGroups).GetAll(), Is.Empty);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Group_writes_copy_borrowed_payloads_and_commit_or_discard(bool commit)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtNodePath[] paths = [new([0x80, 0], 9), new([], 0), new([0], 5)];
        TrackingMemoryProvider memoryProvider = new();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None))
        {
            foreach (PbtNodePath path in paths) WriteGroup(batch, path, BranchNode(1), memoryProvider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            if (commit) batch.Commit();
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        PbtNodePath[] expected = commit ? [new([], 0), new([0], 4), new([0x80], 8)] : [];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.EnumerateNodeGroupKeys(), Is.EqualTo(expected));
            Assert.That(reader.CurrentState, Is.EqualTo(commit ? new StateId(1, default) : StateId.PreGenesis));
            foreach (PbtNodePath path in paths)
                Assert.That(ReadNode(reader, path), commit ? Is.EqualTo(BranchNode(1)) : Is.Null);
        }
    }

    [Test]
    public void Invalid_group_keys_and_payloads_are_rejected_before_staging()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtNodePath groupKey = new([], 0);
        PbtNodePath invalidKey = new([0], 1);
        using RefCountingMemory malformed = RefCountingMemory.Wrapping(new byte[PbtNodeGroupCodec.TrailerLength]);
        using IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None);
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => batch.SetNodeGroup(invalidKey, null), Throws.ArgumentException);
            Assert.That(() => reader.GetNodeGroup(invalidKey), Throws.ArgumentException);
            Assert.That(() => batch.SetNodeGroup(groupKey, malformed), Throws.TypeOf<InvalidDataException>());
        }
        batch.Commit();
        Assert.That(db.GetColumnDb(PbtColumns.NodeGroups).GetAll(), Is.Empty);
        Assert.That(malformed.GetSpan().Length, Is.EqualTo(PbtNodeGroupCodec.TrailerLength));
    }

    [Test]
    public void Node_group_lease_survives_reader_and_persistence_changes_until_disposed()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        PbtRocksDbConfigAdjuster adjuster = new(Substitute.For<IRocksDbConfigFactory>(), dbConfig, new PbtConfig());
        ColumnsDb<PbtColumns> db = new(dbPath.Path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig,
            adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>());
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtNodePath path = new([0], 1);
        PbtNodePath groupKey = PbtFourLevelGroupGeometry.GroupKeyOf(path);
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
                Assert.That(new PbtNodeGroupReader(groupKey, payload.GetSpan()).GetNode(PbtFourLevelGroupGeometry.PositionOf(path)).ToArray(),
                    Is.EqualTo(originalNode));
            }

            using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
                new StateId(1, default), new StateId(2, default), default, WriteFlags.None))
            {
                WriteGroup(batch, path, replacementNode);
                batch.Commit();
            }

            Assert.That(new PbtNodeGroupReader(groupKey, payload.GetSpan()).GetNode(PbtFourLevelGroupGeometry.PositionOf(path)).ToArray(),
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
            Assert.That(metadata.Get(SchemaEpochKey), Is.EqualTo(Epoch(10)));
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
        PbtFullKey leaf = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateStagingWriteBatch(WriteFlags.None))
        {
            batch.SetLeaf(leaf, TestItem.KeccakA.ValueHash256);
            batch.Commit();
        }

        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.GetColumnDb(PbtColumns.FullLeaves).Get(leaf.Bytes), Is.Not.Null);
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
            batch.SetLeaf(PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey), TestItem.KeccakA.ValueHash256);
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
        PbtFullKey stagedLeaf = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        using (IPbtPersistence.IWriteBatch staging = persistence.CreateStagingWriteBatch(WriteFlags.None))
        {
            staging.SetLeaf(stagedLeaf, TestItem.KeccakA.ValueHash256);
            staging.Commit();
        }

        db.FailNextCommit = true;
        IPbtPersistence.IWriteBatch final = persistence.CreateWriteBatch(
            StateId.PreGenesis,
            new StateId(7, TestItem.KeccakB.ValueHash256),
            TestItem.KeccakA.ValueHash256,
            WriteFlags.None);
        PbtFullKey nodeKey = new([0x80]);
        WriteGroup(final, new PbtNodePath([], 0), PbtNodeCodec.Encode(new PbtLeafNode(nodeKey, TestItem.KeccakA.Bytes)));

        Assert.That(() => final.Commit(), Throws.TypeOf<IOException>());
        final.Dispose();
        IDb metadata = inner.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.GetColumnDb(PbtColumns.FullLeaves).Get(stagedLeaf.Bytes), Is.Not.Null);
            Assert.That(inner.GetColumnDb(PbtColumns.NodeGroups).GetAll(), Is.Empty);
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
        yield return new TestCaseData(new byte[] { 9 }, null, null, false).SetName("Rejects_malformed_epoch");
        yield return new TestCaseData(Epoch(10), new byte[] { 0 }, null, false).SetName("Rejects_malformed_current_state");
        yield return new TestCaseData(Epoch(10), null, Array.Empty<byte>(), false).SetName("Rejects_empty_validity");
        yield return new TestCaseData(Epoch(10), null, new byte[] { 2 }, false).SetName("Rejects_unknown_validity");
        yield return new TestCaseData(Epoch(10), null, new byte[] { 1 }, false).SetName("Rejects_validity_without_current_state");
        yield return new TestCaseData(Epoch(10), CurrentState(), null, false).SetName("Rejects_current_state_without_validity");
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

    [TestCase(PbtColumns.FullLeaves)]
    [TestCase(PbtColumns.NodeGroups)]
    [TestCase(PbtColumns.CodeReferences)]
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

    private static RefCountingMemory EncodeGroup(IRefCountingMemoryProvider? memoryProvider, params PbtNodeRecord[] records)
    {
        BufferWriter writer = new(memoryProvider ?? PooledRefCountingMemoryProvider.Instance);
        try
        {
            PbtNodeGroupCodec.Encode(ref writer, PbtFourLevelGroupGeometry.GroupKeyOf(records[0].Path), records);
            return writer.Detach()!;
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteGroup(IPbtPersistence.IWriteBatch batch, PbtNodePath path, byte[] node, IRefCountingMemoryProvider? memoryProvider = null)
    {
        using RefCountingMemory payload = EncodeGroup(memoryProvider, new PbtNodeRecord(path, node));
        batch.SetNodeGroup(PbtFourLevelGroupGeometry.GroupKeyOf(path), payload);
    }

    private static byte[]? ReadNode(IPbtPersistence.IReader reader, PbtNodePath path)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        using RefCountingMemory? payload = reader.GetNodeGroup(location.GroupKey);
        if (payload is null) return null;
        PbtNodeGroupReader group = new(location.GroupKey, payload.GetSpan());
        return group.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding) ? encoding.ToArray() : null;
    }

    private static byte[] BranchNode(byte marker) => PbtNodeCodec.Encode(new PbtBranchNode(
        new PbtBitPrefix([], 0),
        new ValueHash256(Value(marker)),
        new ValueHash256(Value((byte)(marker + 1)))));

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
            if (key == PbtColumns.NodeGroups)
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
