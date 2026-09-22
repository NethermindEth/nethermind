// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class ClearColumnsTests
{
    // Regression: the flat snap-sync clear (FlatSnapTrieFactory.EnsureInitialize -> Clear) must keep the
    // on-disk format markers, otherwise a re-synced RLP DB is later misread as legacy raw, leading to a
    // 33-byte slot value being read as raw and overflowing the slot buffer. Only the state metadata resets.
    [Test]
    public void ClearAllColumns_PreservesFormatMarkers_ResetsStateMetadata_AndWipesData()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);

        BasePersistence.SetLayout(metadata, FlatLayout.Flat); // writes Layout + SlotEncoding=Rlp
        BasePersistence.SetCurrentState(metadata,
            new StateId(123, new ValueHash256("0x1111111111111111111111111111111111111111111111111111111111111111")));

        byte[] slotKey = Bytes.FromHexString("0x0102");
        db.GetColumnDb(FlatDbColumns.Storage)[slotKey] = Bytes.FromHexString("0xabcdef");
        Assert.That(BasePersistence.ReadWipedForSync(metadata), Is.False);

        BasePersistence.ClearAllColumns(db);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BasePersistence.ReadLayout(metadata), Is.EqualTo(FlatLayout.Flat));
            Assert.That(BasePersistence.ReadSlotEncoding(metadata), Is.EqualTo(BasePersistence.SlotEncodingRlp));
            Assert.That(BasePersistence.ReadCurrentState(metadata),
                Is.EqualTo(new StateId(ulong.MaxValue, ValueKeccak.EmptyTreeHash)));
            Assert.That(BasePersistence.ReadWipedForSync(metadata), Is.True);
            Assert.That(db.GetColumnDb(FlatDbColumns.Storage).Get(slotKey), Is.Null);
        }
    }

    // Acknowledging before the flush is durable would let a crash leave a half-wiped DB that the next start
    // no longer treats as repaired.
    [Test]
    public void Clear_flushes_the_wipe_before_acknowledging_the_repair()
    {
        using ClearOrderSpyColumnsDb db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance);

        persistence.Clear();

        Assert.That(db.Events, Is.EqualTo(new[] { ClearOrderSpyColumnsDb.FlushEvent, ClearOrderSpyColumnsDb.AcknowledgeEvent }));
    }

    private static IEnumerable<TestCaseData> WipeMarkerLifecycleCases()
    {
        foreach ((string name, Func<IColumnsDb<FlatDbColumns>, IPersistence> create) in PersistenceFactories())
        {
            yield return new TestCaseData(create, true, true).SetName($"{name}: sync batch keeps the wipe marker");
            yield return new TestCaseData(create, false, false).SetName($"{name}: persisted state clears the wipe marker");
        }
    }

    private static IEnumerable<(string, Func<IColumnsDb<FlatDbColumns>, IPersistence>)> PersistenceFactories()
    {
        yield return (nameof(RocksDbPersistence), static db => new RocksDbPersistence(db, LimboLogs.Instance));
        yield return (nameof(PreimageRocksdbPersistence), static db => new PreimageRocksdbPersistence(db, LimboLogs.Instance, FlatLayout.PreimageFlat));
        yield return (nameof(FlatInTriePersistence), static db => new FlatInTriePersistence(db, LimboLogs.Instance));
    }

    // The sync that follows a wipe never advances the state pointer, so on a restart mid-sync only the wipe marker
    // keeps FlatStateActivationPolicy on flat when a migrated node still holds leftover patricia state. Once a
    // completed sync persists a state pointer, the DB is no longer awaiting its resync.
    [TestCaseSource(nameof(WipeMarkerLifecycleCases))]
    public void Wipe_marker_lasts_until_a_state_pointer_is_persisted(
        Func<IColumnsDb<FlatDbColumns>, IPersistence> create, bool syncBatch, bool expectedWiped)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = create(db);
        persistence.Clear();

        StateId to = syncBatch ? StateId.Sync : new StateId(1, ValueKeccak.EmptyTreeHash);
        using (persistence.CreateWriteBatch(syncBatch ? StateId.Sync : StateId.PreGenesis, to)) { }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistence.WasWipedForSync, Is.EqualTo(expectedWiped));
            Assert.That(reader.CurrentState, Is.EqualTo(syncBatch ? StateId.PreGenesis : to));
        }
    }

    // FlatStateActivationPolicy reads the state pointer to tell "flat holds state" from "flat is empty", so a
    // wipe that crashes midway must not have reset it yet: it has to be the last write, after every data column.
    [Test]
    public void ClearAllColumns_resets_current_state_and_marks_the_wipe_after_every_data_column_in_the_last_batch()
    {
        using MemColumnsDb<FlatDbColumns> inner = new();
        BasePersistence.SetCurrentState(inner.GetColumnDb(FlatDbColumns.Metadata),
            new StateId(123, new ValueHash256("0x1111111111111111111111111111111111111111111111111111111111111111")));
        // More keys than one batch holds, so several chunks are committed before the pointer reset.
        const int keysPerColumn = 6_000;
        foreach (FlatDbColumns column in new[] { FlatDbColumns.Account, FlatDbColumns.Storage })
        {
            IDb columnDb = inner.GetColumnDb(column);
            for (int i = 0; i < keysPerColumn; i++)
            {
                columnDb[BitConverter.GetBytes(i)] = [1];
            }
        }

        WriteOrderSpyColumnsDb db = new(inner);
        BasePersistence.ClearAllColumns(db);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Events.Count(static e => e == WriteOrderSpyColumnsDb.CommitEvent), Is.GreaterThan(1));
            Assert.That(db.Events.Count(static e => e == nameof(FlatDbColumns.Metadata)), Is.EqualTo(2));
            Assert.That(db.Events.TakeLast(3), Is.EqualTo(new[] { nameof(FlatDbColumns.Metadata), nameof(FlatDbColumns.Metadata), WriteOrderSpyColumnsDb.CommitEvent }));
            Assert.That(BasePersistence.ReadCurrentState(inner.GetColumnDb(FlatDbColumns.Metadata)), Is.EqualTo(StateId.PreGenesis));
            Assert.That(BasePersistence.ReadWipedForSync(inner.GetColumnDb(FlatDbColumns.Metadata)), Is.True);
        }
    }

    /// <summary>Records, in order, the flush and repair-acknowledge calls made against the columns DB.</summary>
    private sealed class ClearOrderSpyColumnsDb : SnapshotableMemColumnsDb<FlatDbColumns>, IDbMeta
    {
        public const string FlushEvent = "flush";
        public const string AcknowledgeEvent = "acknowledge";

        public List<string> Events { get; } = [];

        void IDbMeta.Flush(bool onlyWal)
        {
            Events.Add(FlushEvent);
            base.Flush(onlyWal);
        }

        void IDbMeta.AcknowledgeRepair() => Events.Add(AcknowledgeEvent);
    }

    /// <summary>Records, in order, the column of every write staged through its batches and each batch commit.</summary>
    private sealed class WriteOrderSpyColumnsDb(MemColumnsDb<FlatDbColumns> inner) : IColumnsDb<FlatDbColumns>
    {
        public const string CommitEvent = "commit";

        public List<string> Events { get; } = [];

        public IDb GetColumnDb(FlatDbColumns key) => inner.GetColumnDb(key);
        public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => new RecordingColumnsWriteBatch(inner.StartWriteBatch(), Events);
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingColumnsWriteBatch(IColumnsWriteBatch<FlatDbColumns> inner, List<string> events) : IColumnsWriteBatch<FlatDbColumns>
    {
        public IWriteBatch GetColumnBatch(FlatDbColumns key) => new RecordingWriteBatch(inner.GetColumnBatch(key), key, events);
        public void Clear() => inner.Clear();

        public void Dispose()
        {
            inner.Dispose();
            events.Add(WriteOrderSpyColumnsDb.CommitEvent);
        }
    }

    private sealed class RecordingWriteBatch(IWriteBatch inner, FlatDbColumns column, List<string> events) : IWriteBatch
    {
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            events.Add(column.ToString());
            inner.Set(key, value, flags);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.Merge(key, value, flags);
        public void Clear() => inner.Clear();

        // The column batches are owned and committed by the columns batch this view was taken from.
        public void Dispose() { }
    }
}
