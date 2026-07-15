// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtLeafColumnRoutingTests
{
    [Test]
    public void LeafBlobs_AreStoredInTheirZoneColumn()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db);

        Stem accountStem = PbtKeyDerivation.AccountHeaderStem(TestItem.AddressA);
        Stem codeStem = PbtKeyDerivation.CodeOverflowStem(TestItem.KeccakA.ValueHash256, 300, out _);
        Stem storageStem = PbtKeyDerivation.StorageStem(TestItem.AddressA, 1000, out _);

        byte[] accountBlob = Bytes.FromHexString("0xaa");
        byte[] codeBlob = Bytes.FromHexString("0xbb");
        byte[] storageBlob = Bytes.FromHexString("0xcc");

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakB.ValueHash256)))
        {
            batch.SetLeafBlob(accountStem, accountBlob);
            batch.SetLeafBlob(codeStem, codeBlob);
            batch.SetLeafBlob(storageStem, storageBlob);
        }

        // each blob lives in exactly its zone's column and nowhere else
        AssertOnlyIn(db, accountStem, PbtColumns.AccountLeaves, accountBlob);
        AssertOnlyIn(db, codeStem, PbtColumns.CodeLeaves, codeBlob);
        AssertOnlyIn(db, storageStem, PbtColumns.StorageLeaves, storageBlob);
    }

    private static void AssertOnlyIn(SnapshotableMemColumnsDb<PbtColumns> db, in Stem stem, PbtColumns expected, byte[] blob)
    {
        byte[] key = stem.Bytes.ToArray();
        foreach (PbtColumns column in new[] { PbtColumns.AccountLeaves, PbtColumns.CodeLeaves, PbtColumns.StorageLeaves })
        {
            byte[]? stored = db.GetColumnDb(column)[key];
            if (column == expected)
            {
                Assert.That(stored, Is.EqualTo(blob), $"expected blob in {column}");
            }
            else
            {
                Assert.That(stored, Is.Null, $"blob leaked into {column}");
            }
        }
    }
}
