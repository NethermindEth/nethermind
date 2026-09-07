// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
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
        PbtNodePath node = new([], 0);
        ValueHash256 value = TestItem.KeccakA.ValueHash256;
        byte[] nodeEncoding = PbtNodeCodec.Encode(new PbtLeafNode(leaf, value));

        using PbtNodeGroupStore store = new();
        store.SetNode(node, nodeEncoding);
        using RefCountingMemory? group = store.GetNodeGroup(node);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, value), value, WriteFlags.None))
        {
            batch.SetLeaf(leaf, value);
            batch.SetNodeGroup(node, group);
            batch.Commit();
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using RefCountingMemory? persistedGroup = reader.GetNodeGroup(node);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetLeaf(leaf), Is.EqualTo(value));
            Assert.That(persistedGroup!.GetSpan().ToArray(), Is.EqualTo(group!.GetSpan().ToArray()));
            Assert.That(db.GetColumnDb(PbtColumns.AccountLeaves).GetAll(), Is.Empty);
            Assert.That(db.GetColumnDb(PbtColumns.NodeGroups).GetAll(), Is.Not.Empty);
        }
    }
}
