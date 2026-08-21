// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
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

    // Regression: wiping the columns key by key is what makes a restart mid snap sync look like a hang - the
    // scan held EnsureInitialize for ~20 minutes on a mainnet DB abandoned at 19% of the range phase, with no
    // state requests dispatched and nothing logged. Backends that can delete a whole column at once must be
    // asked to, not scanned.
    [Test]
    public void ClearAllColumns_UsesTheBulkDeleteWhenTheBackendHasOne()
    {
        using BulkDeleteColumnsDb db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);

        BasePersistence.SetLayout(metadata, FlatLayout.Flat);
        BasePersistence.SetCurrentState(metadata,
            new StateId(123, new ValueHash256("0x1111111111111111111111111111111111111111111111111111111111111111")));

        byte[] slotKey = Bytes.FromHexString("0x0102");
        db.GetColumnDb(FlatDbColumns.Storage)[slotKey] = Bytes.FromHexString("0xabcdef");

        BasePersistence.ClearAllColumns(db);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Column(FlatDbColumns.Storage).BulkDeletes, Is.EqualTo(1));
            Assert.That(db.GetColumnDb(FlatDbColumns.Storage).Get(slotKey), Is.Null);

            // The metadata column is not wiped, only its state marker is reset, so it is never bulk deleted.
            Assert.That(db.Column(FlatDbColumns.Metadata).BulkDeletes, Is.Zero);
            Assert.That(BasePersistence.ReadLayout(metadata), Is.EqualTo(FlatLayout.Flat));
            Assert.That(BasePersistence.ReadCurrentState(metadata), Is.EqualTo(StateId.PreGenesis));
        }
    }

    private sealed class BulkDeleteColumnsDb : IColumnsDb<FlatDbColumns>
    {
        private readonly Dictionary<FlatDbColumns, BulkDeleteMemDb> _columns = [];

        public BulkDeleteMemDb Column(FlatDbColumns key) =>
            _columns.TryGetValue(key, out BulkDeleteMemDb? db) ? db : _columns[key] = new BulkDeleteMemDb();

        public IDb GetColumnDb(FlatDbColumns key) => Column(key);
        public IEnumerable<FlatDbColumns> ColumnKeys => _columns.Keys;
        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<FlatDbColumns>(this);
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => throw new NotSupportedException();
        public void Flush(bool onlyWal = false) { }
        public void Dispose() { }
    }

    // Re-declares IDb so that the interface maps TryDeleteAll here instead of to the default implementation
    // that MemDb picks up.
    private sealed class BulkDeleteMemDb : MemDb, IDb
    {
        public int BulkDeletes { get; private set; }

        public bool TryDeleteAll()
        {
            BulkDeletes++;
            Clear();
            return true;
        }
    }
}
