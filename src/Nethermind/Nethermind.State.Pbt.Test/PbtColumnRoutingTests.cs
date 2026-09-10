// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtColumnRoutingTests
{
    [Test]
    public void TypedValuesAndNodes_AreStoredWithoutSplitLeaves()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        PbtFullKey leaf = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        PbtStorageFullKey storageKey = PbtStateKey.Storage(TestItem.AddressA, 0);
        Account account = new(7, 9);
        EvmWord slot = EvmWordSlot.FromStripped(TestItem.KeccakA.Bytes);
        CodeInfo code = new(TestItem.KeccakB.Bytes.ToArray());
        PbtNodePath node = new([], 0);
        ValueHash256 value = TestItem.KeccakA.ValueHash256;
        byte[] nodeEncoding = PbtNodeCodec.EncodeLeaf(leaf, value.Bytes);

        using PbtNodeGroupStore store = new();
        store.SetNode(node, nodeEncoding);
        using RefCountingMemory? group = store.GetNodeGroup(node);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, value), value, WriteFlags.None))
        {
            batch.SetAccount(addressHash, account);
            batch.SetSlot(storageKey, slot);
            batch.SetCode(value, code);
            batch.SetNodeGroup(node, group);
            batch.Commit();
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using RefCountingMemory? persistedGroup = reader.GetNodeGroup(node);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccount(addressHash), Is.EqualTo(account));
            Assert.That(reader.GetSlot(storageKey), Is.EqualTo(slot));
            Assert.That(reader.GetCode(value), Is.EqualTo(code));
            Assert.That(db.GetColumnDb(PbtColumns.Accounts).Get(addressHash.Bytes), Is.Not.Null);
            Assert.That(db.GetColumnDb(PbtColumns.Storages).Get(storageKey.Bytes), Is.Not.Null);
            Assert.That(db.GetColumnDb(PbtColumns.Codes).Get(value.Bytes), Is.EqualTo(code.Code.ToArray()));
            Assert.That(db.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
            Assert.That(persistedGroup!.GetSpan().ToArray(), Is.EqualTo(group!.GetSpan().ToArray()));
            Assert.That(db.GetColumnDb(PbtColumns.AccountLeaves).GetAll(), Is.Empty);
            Assert.That(db.GetColumnDb(PbtColumns.NodeGroups).GetAll(), Is.Empty);
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8), Is.EqualTo(group.GetSpan().ToArray()));
        }
    }
}
