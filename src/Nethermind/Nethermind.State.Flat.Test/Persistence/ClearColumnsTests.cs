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

        BasePersistence.ClearAllColumns(db);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BasePersistence.ReadLayout(metadata), Is.EqualTo(FlatLayout.Flat));
            Assert.That(BasePersistence.ReadSlotEncoding(metadata), Is.EqualTo(BasePersistence.SlotEncodingRlp));
            Assert.That(BasePersistence.ReadCurrentState(metadata),
                Is.EqualTo(new StateId(ulong.MaxValue, ValueKeccak.EmptyTreeHash)));
            Assert.That(db.GetColumnDb(FlatDbColumns.Storage).Get(slotKey), Is.Null);
        }
    }

    [Test]
    public void Clear_acknowledges_repair_after_wiping_columns()
    {
        using AcknowledgeSpyColumnsDb db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance);

        persistence.Clear();

        Assert.That(db.AcknowledgeRepairCalls, Is.EqualTo(1));
    }

    // FlatStateActivationPolicy reads the state pointer to tell "flat holds state" from "flat is empty", so a
    // wipe that crashes midway must not have reset it yet: it has to be the last write, after every data column.
    [Test]
    public void ClearAllColumns_resets_current_state_after_every_data_column_in_the_last_batch()
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
            Assert.That(db.Events.Count(static e => e == nameof(FlatDbColumns.Metadata)), Is.EqualTo(1));
            Assert.That(db.Events.TakeLast(2), Is.EqualTo(new[] { nameof(FlatDbColumns.Metadata), WriteOrderSpyColumnsDb.CommitEvent }));
            Assert.That(BasePersistence.ReadCurrentState(inner.GetColumnDb(FlatDbColumns.Metadata)), Is.EqualTo(StateId.PreGenesis));
        }
    }

    private sealed class AcknowledgeSpyColumnsDb : SnapshotableMemColumnsDb<FlatDbColumns>, IDbMeta
    {
        public int AcknowledgeRepairCalls { get; private set; }
        void IDbMeta.AcknowledgeRepair() => AcknowledgeRepairCalls++;
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
