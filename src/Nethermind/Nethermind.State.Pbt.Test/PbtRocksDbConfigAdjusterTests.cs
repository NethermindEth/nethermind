// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
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
        AccountLeavesRocksDbOptions = $"column={nameof(PbtColumns.AccountLeaves)};",
        CodeLeavesRocksDbOptions = $"column={nameof(PbtColumns.CodeLeaves)};",
        StorageLeavesRocksDbOptions = $"column={nameof(PbtColumns.StorageLeaves)};",
        AccountTrieNodesRocksDbOptions = $"column={nameof(PbtColumns.AccountTrieNodes)};",
        CodeTrieNodesRocksDbOptions = $"column={nameof(PbtColumns.CodeTrieNodes)};",
        StorageTrieNodesRocksDbOptions = $"column={nameof(PbtColumns.StorageTrieNodes)};",
    };

    [TestCase(PbtColumns.Metadata)]
    [TestCase(PbtColumns.AccountLeaves)]
    [TestCase(PbtColumns.CodeLeaves)]
    [TestCase(PbtColumns.StorageLeaves)]
    [TestCase(PbtColumns.AccountTrieNodes)]
    [TestCase(PbtColumns.CodeTrieNodes)]
    [TestCase(PbtColumns.StorageTrieNodes)]
    public void EveryColumnGetsTheGlobalThenSharedThenItsOwnOptions(PbtColumns column)
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), column.ToString());

        Assert.That(config.RocksDbOptions, Is.EqualTo($"global=1;shared=1;column={column};"));
    }

    [TestCase(PbtColumns.Accounts, PbtColumns.AccountLeaves)]
    [TestCase(PbtColumns.Storages, PbtColumns.StorageLeaves)]
    [TestCase(PbtColumns.Codes, PbtColumns.CodeLeaves)]
    [TestCase(PbtColumns.NodeGroups, PbtColumns.StorageTrieNodes)]
    [TestCase(PbtColumns.CodeReferences, PbtColumns.CodeLeaves)]
    public void TypedColumnsReuseTheirDomainOptions(PbtColumns column, PbtColumns optionsColumn)
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), column.ToString());

        Assert.That(config.RocksDbOptions, Is.EqualTo($"global=1;shared=1;column={optionsColumn};"));
    }

    [Test]
    public void PbtDatabaseItselfGetsTheSharedOptionsOnly()
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), null);

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
        PbtFullKey key = new([0x80]);
        PbtNodePath path = new([], 0);
        byte[] encoding = PbtNodeCodec.EncodeLeaf(key, new byte[ValueHash256.MemorySize]);
        StateId state = new(1, new ValueHash256([1]));
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        Account account = new(7, 9, TestItem.KeccakB, TestItem.KeccakC);
        PbtFullKey storageKey = PbtStateKey.Storage(TestItem.AddressA, 64);
        EvmWord slot = EvmWordSlot.FromStripped(TestItem.KeccakD.Bytes);
        CodeInfo code = new(TestItem.KeccakA.Bytes.ToArray());

        using (ColumnsDb<PbtColumns> db = NewDb())
        {
            PbtRocksDbPersistence persistence = new(db, new PbtConfig());
            using IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, state, default, WriteFlags.None);
            batch.SetAccount(addressHash, account);
            batch.SetSlot(storageKey, slot);
            batch.SetCode(account.CodeHash.ValueHash256, code);
            BufferWriter writer = new(PooledRefCountingMemoryProvider.Instance);
            try
            {
                PbtNodeGroupCodec.Encode(ref writer, path, [new PbtNodeRecord(path, encoding)]);
                using RefCountingMemory payload = writer.Detach()!;
                batch.SetNodeGroup(path, payload);
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
            using RefCountingMemory payload = reader.GetNodeGroup(path)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(state));
                Assert.That(reader.GetAccount(addressHash), Is.EqualTo(account));
                Assert.That(reader.GetSlot(storageKey), Is.EqualTo(slot));
                Assert.That(reader.GetCode(account.CodeHash.ValueHash256), Is.EqualTo(code));
                Assert.That(db.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
                Assert.That(new PbtNodeGroupReader(path, payload.GetSpan()).GetNode(PbtFourLevelGroupGeometry.RootPosition).ToArray(), Is.EqualTo(encoding));
                Assert.That(reader.EnumerateNodeGroupKeys(), Is.EqualTo(new[] { path }));
            }
        }

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
