// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtColumnRoutingTests
{
    [Test]
    public void CanonicalLeavesAndNodes_AreStoredInTheirCanonicalColumns()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtFullKey leaf = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        PbtFullKey node = new([0x01, 0x02]);
        ValueHash256 value = TestItem.KeccakA.ValueHash256;

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, value), value, WriteFlags.None))
        {
            batch.SetLeaf(leaf, value);
            batch.SetNode(node, [0x11]);
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetLeaf(leaf), Is.EqualTo(value));
            Assert.That(reader.GetNode(node), Is.EqualTo(new byte[] { 0x11 }));
            Assert.That(db.GetColumnDb(PbtColumns.AccountLeaves).GetAll(), Is.Empty);
            Assert.That(db.GetColumnDb(PbtColumns.CompressedNodes).GetAll(), Is.Not.Empty);
        }
    }
}
