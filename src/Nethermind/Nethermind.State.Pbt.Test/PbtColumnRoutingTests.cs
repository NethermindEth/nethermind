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

public class PbtColumnRoutingTests
{
    private static readonly PbtColumns[] LeafColumns = [PbtColumns.AccountLeaves, PbtColumns.CodeLeaves, PbtColumns.StorageLeaves];
    private static readonly PbtColumns[] TrieNodeColumns = [PbtColumns.AccountTrieNodes, PbtColumns.CodeTrieNodes, PbtColumns.StorageTrieNodes];

    private static Stem AccountStem => PbtKeyDerivation.AccountHeaderStem(TestItem.AddressA);
    private static Stem CodeStem => PbtKeyDerivation.CodeOverflowStem(TestItem.KeccakA.ValueHash256, 300, out _);
    private static Stem StorageStem => PbtKeyDerivation.StorageStem(TestItem.AddressA, 1000, out _);

    [Test]
    public void LeafBlobs_AreStoredInTheirZoneColumn()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");

        byte[] accountBlob = Bytes.FromHexString("0xaa");
        byte[] codeBlob = Bytes.FromHexString("0xbb");
        byte[] storageBlob = Bytes.FromHexString("0xcc");

        using (IPbtPersistence.IWriteBatch batch = StartBatch(db))
        {
            batch.SetLeafBlob(AccountStem, accountBlob);
            batch.SetLeafBlob(CodeStem, codeBlob);
            batch.SetLeafBlob(StorageStem, storageBlob);
        }

        AssertOnlyIn(db, AccountStem.Bytes.ToArray(), PbtColumns.AccountLeaves, accountBlob, LeafColumns);
        AssertOnlyIn(db, CodeStem.Bytes.ToArray(), PbtColumns.CodeLeaves, codeBlob, LeafColumns);
        AssertOnlyIn(db, StorageStem.Bytes.ToArray(), PbtColumns.StorageLeaves, storageBlob, LeafColumns);
    }

    /// <summary>The depth-0 root has no zone nibble yet, so it shares the account column.</summary>
    [Test]
    public void TrieNodes_AreStoredInTheirZoneColumn_WithTheRootUnderAccount()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");

        TrieNodeKey accountKey = TrieNodeKey.For(PbtLayout.TrieNodeGroupLevelsPerGroup, AccountStem);
        TrieNodeKey codeKey = TrieNodeKey.For(PbtLayout.TrieNodeGroupLevelsPerGroup, CodeStem);
        TrieNodeKey storageKey = TrieNodeKey.For(PbtLayout.TrieNodeGroupLevelsPerGroup, StorageStem);

        byte[] rootNode = Bytes.FromHexString("0x11");
        byte[] accountNode = Bytes.FromHexString("0x22");
        byte[] codeNode = Bytes.FromHexString("0x33");
        byte[] storageNode = Bytes.FromHexString("0x44");

        using (IPbtPersistence.IWriteBatch batch = StartBatch(db))
        {
            batch.SetTrieNode(TrieNodeKey.Root, rootNode);
            batch.SetTrieNode(accountKey, accountNode);
            batch.SetTrieNode(codeKey, codeNode);
            batch.SetTrieNode(storageKey, storageNode);
        }

        AssertOnlyIn(db, TrieNodeKey.Root.ToDbKey(), PbtColumns.AccountTrieNodes, rootNode, TrieNodeColumns);
        AssertOnlyIn(db, accountKey.ToDbKey(), PbtColumns.AccountTrieNodes, accountNode, TrieNodeColumns);
        AssertOnlyIn(db, codeKey.ToDbKey(), PbtColumns.CodeTrieNodes, codeNode, TrieNodeColumns);
        AssertOnlyIn(db, storageKey.ToDbKey(), PbtColumns.StorageTrieNodes, storageNode, TrieNodeColumns);
    }

    private static IPbtPersistence.IWriteBatch StartBatch(SnapshotableMemColumnsDb<PbtColumns> db) =>
        new PbtRocksDbPersistence(db).CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakB.ValueHash256), WriteFlags.None);

    private static void AssertOnlyIn(SnapshotableMemColumnsDb<PbtColumns> db, byte[] key, PbtColumns expected, byte[] value, PbtColumns[] candidates)
    {
        foreach (PbtColumns column in candidates)
        {
            byte[]? stored = db.GetColumnDb(column)[key];
            if (column == expected)
            {
                Assert.That(stored, Is.EqualTo(value), $"expected value in {column}");
            }
            else
            {
                Assert.That(stored, Is.Null, $"value leaked into {column}");
            }
        }
    }
}
