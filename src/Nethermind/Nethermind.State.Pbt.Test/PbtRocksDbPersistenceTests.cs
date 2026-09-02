// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtRocksDbPersistenceTests
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private static ReadOnlySpan<byte> ValidStateKey => "validState"u8;

    [Test]
    public void Completed_epoch_8_store_reopens_and_serves_canonical_records()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtFullKey leaf = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        PbtNodeLocator locator = new([0xA0], 3);
        ValueHash256 value = TestItem.KeccakA.ValueHash256;
        StateId state = new(7, TestItem.KeccakB.ValueHash256);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, state, value, WriteFlags.None))
        {
            batch.SetLeaf(leaf, value);
            batch.SetNode(locator, [0x11, 0x22]);
            batch.Commit();
        }

        PbtRocksDbPersistence reopened = new(db, new PbtConfig());
        using IPbtPersistence.IReader reader = reopened.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(state));
            Assert.That(reader.CurrentRoot, Is.EqualTo(value));
            Assert.That(reader.GetLeaf(leaf), Is.EqualTo(value));
            Assert.That(reader.GetNode(locator), Is.EqualTo(new byte[] { 0x11, 0x22 }));
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get(ValidStateKey), Is.EqualTo(new byte[] { 1 }));
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
            Assert.That(metadata.Get(SchemaEpochKey), Is.EqualTo(Epoch(8)));
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
        final.SetNode(new PbtNodeLocator([0x80], 1), [0x11]);

        Assert.That(() => final.Commit(), Throws.TypeOf<IOException>());
        final.Dispose();
        IDb metadata = inner.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.GetColumnDb(PbtColumns.FullLeaves).Get(stagedLeaf.Bytes), Is.Not.Null);
            Assert.That(inner.GetColumnDb(PbtColumns.CompressedNodes).GetAll(), Is.Empty);
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
        yield return new TestCaseData(new byte[] { 8 }, null, null, false).SetName("Rejects_malformed_epoch");
        yield return new TestCaseData(Epoch(8), new byte[] { 0 }, null, false).SetName("Rejects_malformed_current_state");
        yield return new TestCaseData(Epoch(8), null, Array.Empty<byte>(), false).SetName("Rejects_empty_validity");
        yield return new TestCaseData(Epoch(8), null, new byte[] { 2 }, false).SetName("Rejects_unknown_validity");
        yield return new TestCaseData(Epoch(8), null, new byte[] { 1 }, false).SetName("Rejects_validity_without_current_state");
        yield return new TestCaseData(Epoch(8), CurrentState(), null, false).SetName("Rejects_current_state_without_validity");
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
        ThrowOnCompressedNodesDb db = new(inner);

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig()), Throws.TypeOf<InvalidDataException>());
        Assert.That(db.CompressedNodesAccessed, Is.False);
    }

    [TestCase(PbtColumns.FullLeaves)]
    [TestCase(PbtColumns.CompressedNodes)]
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

    private sealed class ThrowOnCompressedNodesDb(IColumnsDb<PbtColumns> inner) : IColumnsDb<PbtColumns>
    {
        public bool CompressedNodesAccessed { get; private set; }
        public IEnumerable<PbtColumns> ColumnKeys => inner.ColumnKeys;
        public long EstimatedCount => inner.EstimatedCount;

        public IDb GetColumnDb(PbtColumns key)
        {
            if (key == PbtColumns.CompressedNodes)
            {
                CompressedNodesAccessed = true;
                throw new AssertionException("Compressed nodes were accessed before metadata rejection.");
            }
            return inner.GetColumnDb(key);
        }

        public IColumnsWriteBatch<PbtColumns> StartWriteBatch() => inner.StartWriteBatch();
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() => inner.Dispose();
    }
}
