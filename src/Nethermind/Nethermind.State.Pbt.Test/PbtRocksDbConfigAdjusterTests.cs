// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PbtRocksDbConfigAdjusterTests
{
    private static PbtRocksDbConfigAdjuster CreateAdjuster(IRocksDbConfigFactory baseFactory) =>
        new(baseFactory, new DbConfig { RocksDbOptions = "global=1;" }, MarkedConfig());

    /// <summary>Marks every option string with the column it belongs to, so the mapping is what is asserted, not the tuning.</summary>
    private static PbtConfig MarkedConfig() => new()
    {
        RocksDbOptions = "shared=1;",
        MetadataRocksDbOptions = $"column={nameof(PbtColumns.Metadata)};",
        AccountsRocksDbOptions = $"column={nameof(PbtColumns.Accounts)};",
        CodesRocksDbOptions = $"column={nameof(PbtColumns.Codes)};",
        StoragesRocksDbOptions = $"column={nameof(PbtColumns.Storages)};",
        NodeGroupsRocksDbOptions = "column=NodeGroups;",
        CodeReferencesRocksDbOptions = $"column={nameof(PbtColumns.CodeReferences)};",
    };

    [Test]
    public void EveryActiveColumnGetsTheGlobalThenSharedThenItsOwnOptions(
        [Values(PbtColumns.Metadata, PbtColumns.Accounts, PbtColumns.Codes, PbtColumns.Storages,
            PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups,
            PbtColumns.CodeReferences)] PbtColumns column)
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), column.ToString());

        string optionsColumn = column is PbtColumns.AccountNodeGroups or PbtColumns.CodeNodeGroups or PbtColumns.StorageNodeGroups
            ? "NodeGroups" : column.ToString();
        Assert.That(config.RocksDbOptions, Is.EqualTo($"global=1;shared=1;column={optionsColumn};"));
    }

    [Test]
    public void LegacyColumnsAndDatabaseItselfGetTheSharedOptionsOnly(
        [Values(null, "NodeGroups", nameof(PbtColumns.FullLeaves), nameof(PbtColumns.AccountLeaves), nameof(PbtColumns.CodeLeaves),
            nameof(PbtColumns.StorageLeaves), nameof(PbtColumns.AccountTrieNodes), nameof(PbtColumns.CodeTrieNodes),
            nameof(PbtColumns.StorageTrieNodes))] string? columnName)
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), columnName);

        Assert.That(config.RocksDbOptions, Is.EqualTo("global=1;shared=1;"));
    }

    /// <summary>An option rocksdb does not know fails the database open, which with pbt enabled is the node failing to start.</summary>
    [Test]
    public void DefaultOptionsOfEveryColumnAreAcceptedByRocksDb()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        PbtRocksDbConfigAdjuster adjuster = new(Substitute.For<IRocksDbConfigFactory>(), dbConfig, new PbtConfig());

        using ColumnsDb<PbtColumns> db = new(dbPath.Path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig,
            adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>());

        foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
        {
            db.GetColumnDb(column).Set([(byte)column], [1]);
            Assert.That(db.GetColumnDb(column).Get([(byte)column]), Is.EqualTo(new byte[] { 1 }));
        }
    }

    [Test]
    public void NodeGroupsReopenFromRocksDbAsCanonicalNodes()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        PbtRocksDbConfigAdjuster adjuster = new(Substitute.For<IRocksDbConfigFactory>(), dbConfig, new PbtConfig());
        byte[] widePath = new byte[35];
        widePath[0] = Eip8297KeyDerivation.StorageZone;
        (PbtStorageNodePath Path, PbtColumns Column)[] groups =
        [
            (new PbtStorageNodePath([], 0), PbtColumns.Metadata),
            (new PbtStorageNodePath(Bytes.FromHexString("00"), 4), PbtColumns.AccountNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("80"), 4), PbtColumns.AccountNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("f0"), 4), PbtColumns.StorageNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("00"), 8), PbtColumns.AccountNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("01"), 8), PbtColumns.CodeNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("80"), 8), PbtColumns.AccountNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("ff"), 8), PbtColumns.StorageNodeGroups),
            (new PbtStorageNodePath(widePath, 280), PbtColumns.StorageNodeGroups),
        ];
        PbtStorageNodePath[] expectedPaths = new PbtStorageNodePath[groups.Length];
        for (int index = 0; index < groups.Length; index++) expectedPaths[index] = groups[index].Path;
        byte[] encoding = PbtNodeCodec.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
        StateId state = new(1, TestItem.KeccakA.ValueHash256);
        ValueHash256 treeRoot = TestItem.KeccakD.ValueHash256;
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        Account account = new(7, 9, TestItem.KeccakB, TestItem.KeccakC);
        PbtStorageFullKey storageKey = PbtStateKey.Storage(TestItem.AddressA, 64);
        EvmWord slot = EvmWordSlot.FromStripped(TestItem.KeccakD.Bytes);
        CodeInfo code = new(TestItem.KeccakA.Bytes.ToArray());

        using (ColumnsDb<PbtColumns> db = NewDb())
        {
            PbtRocksDbPersistence persistence = new(db, new PbtConfig());
            using IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, state, treeRoot, WriteFlags.None);
            batch.SetAccount(addressHash, account);
            batch.SetSlot(storageKey, slot);
            batch.SetCode(account.CodeHash.ValueHash256, code);
            BufferWriter writer = new(PooledRefCountingMemoryProvider.Instance);
            try
            {
                foreach ((PbtStorageNodePath path, PbtColumns _) in groups)
                {
                    PbtStorageNodePath nodePath = PbtFourLevelGroupGeometry.PathOf(path, NodePosition(path));
                    PbtNodeGroupCodec.Encode(ref writer, path, [new PbtNodeRecord(nodePath, encoding)]);
                    using RefCountingMemory payload = writer.Detach()!;
                    batch.SetNodeGroup(path, payload);
                }
            }
            finally
            {
                writer.Dispose();
            }
            batch.Commit();
        }

        using (ColumnsDb<PbtColumns> db = NewDb())
        {
            PbtRocksDbPersistence persistence = new(db, new PbtConfig());
            using IPbtPersistence.IReader reader = persistence.CreateReader();
            using IEnumerator<PbtStorageNodePath> enumerator = reader.EnumerateNodeGroupKeys().GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True);
            Assert.That(enumerator.Current, Is.EqualTo(expectedPaths[0]));
            Assert.That(enumerator.MoveNext(), Is.True);
            Assert.That(enumerator.Current, Is.EqualTo(expectedPaths[1]));
        }

        using (ColumnsDb<PbtColumns> db = NewDb())
        {
            PbtRocksDbPersistence persistence = new(db, new PbtConfig());
            using IPbtPersistence.IReader reader = persistence.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(state));
                Assert.That(reader.CurrentRoot, Is.EqualTo(treeRoot));
                Assert.That(reader.GetAccount(addressHash), Is.EqualTo(account));
                Assert.That(reader.GetSlot(storageKey), Is.EqualTo(slot));
                Assert.That(reader.GetCode(account.CodeHash.ValueHash256), Is.EqualTo(code));
                Assert.That(db.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
                Assert.That(reader.EnumerateNodeGroupKeys(), Is.EqualTo(expectedPaths));
            }
            foreach ((PbtStorageNodePath path, PbtColumns column) in groups)
            {
                using RefCountingMemory? payload = reader.GetNodeGroup(path);
                Assert.That(payload, Is.Not.Null, $"group {path.BitDepth}:{Convert.ToHexString(path.ToEncodedArray().AsSpan(4))}");
                byte[] storageKeyBytes = path.BitDepth == 0 ? "rootNodeGroup"u8.ToArray() : path.ToEncodedArray();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(db.GetColumnDb(column).Get(storageKeyBytes), Is.EqualTo(payload!.GetSpan().ToArray()));
                    Assert.That(PbtStoreTestExtensions.ReadGroup(path, payload!.GetSpan()).GetNode(NodePosition(path)).ToArray(), Is.EqualTo(encoding));
                }
            }
        }

        static int NodePosition(PbtStorageNodePath path) => path.BitDepth == 0 ? PbtFourLevelGroupGeometry.RootPosition : 0;

        ColumnsDb<PbtColumns> NewDb() => new(dbPath.Path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig,
            adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>());
    }

    [Test]
    public void OtherDatabasesAreLeftToTheBaseFactory()
    {
        IRocksDbConfigFactory baseFactory = Substitute.For<IRocksDbConfigFactory>();
        IRocksDbConfig baseConfig = Substitute.For<IRocksDbConfig>();
        baseFactory.GetForDatabase(nameof(DbNames.Blocks), null).Returns(baseConfig);

        IRocksDbConfig config = CreateAdjuster(baseFactory).GetForDatabase(nameof(DbNames.Blocks), null);

        Assert.That(config, Is.SameAs(baseConfig));
    }
}
