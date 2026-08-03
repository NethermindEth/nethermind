// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtStorageKeyLayoutTests
{
    [Test]
    public void CanonicalStorageKey_RoundTripsAndDeletes()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtFullKey key = PbtStateKey.Storage(TestItem.AddressA, new UInt256(1000));
        ValueHash256 value = TestItem.KeccakA.ValueHash256;
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, value), value, WriteFlags.None)) batch.SetLeaf(key, value);
        using (IPbtPersistence.IReader reader = persistence.CreateReader()) Assert.That(reader.GetLeaf(key), Is.EqualTo(value));
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(new StateId(1, value), new StateId(2, value), value, WriteFlags.None)) batch.SetLeaf(key, null);
        using IPbtPersistence.IReader deleted = persistence.CreateReader();
        Assert.That(deleted.GetLeaf(key), Is.Null);
    }
}
