// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class ClearAllColumnsBatchingTests
{
    // #11442/#11996: seed more keys than one batch, then confirm the column is fully wiped across batch
    // boundaries while the Metadata format markers survive and only CurrentState resets.
    [Test]
    public void ClearAllColumns_clears_data_across_batch_boundaries_and_preserves_format_markers()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        IDb storage = db.GetColumnDb(FlatDbColumns.Storage);

        BasePersistence.SetLayout(metadata, FlatLayout.Flat); // writes Layout + SlotEncoding=Rlp
        BasePersistence.SetCurrentState(metadata,
            new StateId(123, new ValueHash256("0x1111111111111111111111111111111111111111111111111111111111111111")));

        const int keyCount = 25_000; // spans multiple 10k batches
        for (int i = 0; i < keyCount; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            storage.Set(key, [0x01]);
        }

        BasePersistence.ClearAllColumns(db);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.GetAllKeys().Any(), Is.False, "all data should be wiped");
            Assert.That(BasePersistence.ReadLayout(metadata), Is.EqualTo(FlatLayout.Flat));
            Assert.That(BasePersistence.ReadCurrentState(metadata), Is.EqualTo(new StateId(-1, ValueKeccak.EmptyTreeHash)));
        }
    }
}
