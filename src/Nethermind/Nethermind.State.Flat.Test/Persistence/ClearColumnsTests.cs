// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
