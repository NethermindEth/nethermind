// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class FlatBaseStoreConverterTests
{
    private const int AccountShards = 4;
    private const int StorageShards = 8;

    private TempPath _dir = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private ArenaBasePersistence? _arena;

    [SetUp]
    public void SetUp()
    {
        _dir = TempPath.GetTempDirectory();
        // See ArenaBasePersistenceTests: same-version snapshots alias in SnapshotableMemDb's version set.
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>(neverPrune: true);
    }

    [TearDown]
    public void TearDown()
    {
        _arena?.Dispose();
        _db.Dispose();
        _dir.Dispose();
    }

    private static FlatDbConfig ArenaConfig() =>
        new() { BaseStore = FlatBaseStore.Arena, ConvertBaseStore = true };

    private ArenaBasePersistence NewArenaPersistence() => _arena = new ArenaBasePersistence(
        _db, _dir.Path, ArenaConfig(), LimboLogs.Instance, AccountShards, StorageShards);

    private FlatBaseStoreConverter NewConverter(ArenaBasePersistence arena) =>
        new(_db, arena, ArenaConfig(), LimboLogs.Instance);

    private static readonly (Address Address, Account Account)[] SeedAccounts = Enumerable.Range(0, 16)
        .Select(static i => (TestItem.Addresses[i], TestItem.GenerateIndexedAccount(i)))
        .ToArray();

    private static readonly (Address Address, UInt256 Slot, byte[] Value)[] SeedSlots = Enumerable.Range(0, 16)
        .SelectMany(static i => new[]
        {
            (TestItem.Addresses[i], (UInt256)1, new byte[] { (byte)(i + 1) }),
            (TestItem.Addresses[i], UInt256.MaxValue, new byte[] { 0xab, (byte)i }),
        })
        .ToArray();

    private void SeedViaRocks()
    {
        RocksDbPersistence rocks = new(_db, LimboLogs.Instance);
        using IPersistence.IWriteBatch batch = rocks.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None);
        foreach ((Address address, Account account) in SeedAccounts) batch.SetAccount(address, account);
        foreach ((Address address, UInt256 slot, byte[] value) in SeedSlots)
            batch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(value));
    }

    private void AssertSeededStateReadable(IPersistence persistence)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach ((Address address, Account account) in SeedAccounts)
                Assert.That(reader.GetAccount(address), Is.EqualTo(account), address.ToString());
            foreach ((Address address, UInt256 slot, byte[] value) in SeedSlots)
            {
                SlotValue slotValue = default;
                Assert.That(reader.TryGetSlot(address, in slot, ref slotValue), Is.True, $"slot {slot} of {address}");
                Assert.That(slotValue.ToEvmBytes(), Is.EqualTo(value), $"slot {slot} of {address}");
            }

            // Full hash-ordered iteration must surface exactly the seeded accounts.
            List<ValueHash256> expectedKeys = SeedAccounts
                .Select(static e =>
                {
                    ValueHash256 key = ValueKeccak.Zero;
                    e.Address.ToAccountPath.Bytes[..20].CopyTo(key.BytesAsSpan);
                    return key;
                })
                .OrderBy(static k => k, Comparer<ValueHash256>.Default)
                .ToList();
            List<ValueHash256> actualKeys = [];
            using IPersistence.IFlatIterator iterator = reader.CreateAccountIterator();
            while (iterator.MoveNext()) actualKeys.Add(iterator.CurrentKey);
            Assert.That(actualKeys, Is.EqualTo(expectedKeys), "account iteration");
        }
    }

    private int OverlayRowCount() =>
        _db.GetColumnDb(FlatDbColumns.Account).GetAll().Count() +
        _db.GetColumnDb(FlatDbColumns.Storage).GetAll().Count();

    [Test]
    public void Convert_MigratesEverything_EmptiesOverlay_StampsMarker_AndIsIdempotent()
    {
        SeedViaRocks();
        ArenaBasePersistence arena = NewArenaPersistence();
        FlatBaseStoreConverter converter = NewConverter(arena);

        Assert.That(converter.Convert(CancellationToken.None), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(OverlayRowCount(), Is.Zero, "overlay must be empty after conversion");
            Assert.That(ArenaBasePersistence.ReadBaseStoreKind(_db), Is.EqualTo(FlatBaseStore.Arena));
            Assert.That(() => ArenaBasePersistence.ValidateBaseStoreKind(_db, FlatBaseStore.Rocks),
                Throws.TypeOf<InvalidConfigurationException>(), "a Rocks boot on the converted DB must fail loudly");
        }
        AssertSeededStateReadable(arena);

        Assert.That(converter.Convert(CancellationToken.None), Is.False, "second conversion must be a no-op");
        AssertSeededStateReadable(arena);
    }

    [Test]
    public void Convert_OnEmptyDb_IsNoOp()
    {
        ArenaBasePersistence arena = NewArenaPersistence();

        Assert.That(NewConverter(arena).Convert(CancellationToken.None), Is.False);
        Assert.That(ArenaBasePersistence.ReadBaseStoreKind(_db), Is.Null);
    }

    [Test]
    public void CrashBeforeMarker_NextBootReconvertsFromIntactOverlay()
    {
        SeedViaRocks();
        ArenaBasePersistence arena = NewArenaPersistence();

        // Simulate a crash after the shard tables were built and registered but before the marker: the
        // overlay is still intact, so the next boot's conversion rebuilds from it.
        NewConverter(arena).BuildShardTables(CancellationToken.None);
        Assert.That(ArenaBasePersistence.ReadBaseStoreKind(_db), Is.Null);
        arena.Dispose();

        ArenaBasePersistence rebooted = NewArenaPersistence();
        Assert.That(NewConverter(rebooted).Convert(CancellationToken.None), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(OverlayRowCount(), Is.Zero);
            Assert.That(ArenaBasePersistence.ReadBaseStoreKind(_db), Is.EqualTo(FlatBaseStore.Arena));
        }
        AssertSeededStateReadable(rebooted);
    }

    [Test]
    public void CrashAfterMarker_BeforeCleanup_BootsAsArena_LeftoverOverlayShadowsIdentically()
    {
        SeedViaRocks();
        ArenaBasePersistence arena = NewArenaPersistence();

        // Simulate a crash right after the commit point: marker stamped, overlay not yet cleaned.
        FlatBaseStoreConverter converter = NewConverter(arena);
        converter.BuildShardTables(CancellationToken.None);
        converter.CommitConverted();
        arena.Dispose();

        ArenaBasePersistence rebooted = NewArenaPersistence();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NewConverter(rebooted).Convert(CancellationToken.None), Is.False, "marker says Arena — no reconversion");
            Assert.That(OverlayRowCount(), Is.GreaterThan(0), "the crash left the overlay uncleaned");
        }

        // The leftover overlay rows shadow byte-identical base values — reads stay correct...
        AssertSeededStateReadable(rebooted);

        // ...and the next fold reconciles them away.
        rebooted.Fold();
        Assert.That(OverlayRowCount(), Is.Zero);
        AssertSeededStateReadable(rebooted);
    }

    [Test]
    public void Convert_ThenTrieVerify_ReportsNoMismatch()
    {
        using MemDb trieDb = new();
        RawScopedTrieStore trieStore = new(trieDb);
        StateTree stateTree = new(trieStore, LimboLogs.Instance);

        Address contractAddress = TestItem.AddressA;
        (UInt256 Slot, byte[] Value)[] slots = [((UInt256)1, [0x11]), (UInt256.MaxValue, [0x22, 0x33])];
        Hash256 addressHash = Keccak.Compute(contractAddress.Bytes);
        StorageTree storageTree = new((IScopedTrieStore)trieStore.GetStorageTrieNodeResolver(addressHash), LimboLogs.Instance);
        foreach ((UInt256 slot, byte[] value) in slots) storageTree.Set(slot, value);
        storageTree.Commit();

        Account contract = new(1, 100, storageTree.RootHash, Keccak.Compute([1]));
        Account plain = new(2, 200);
        stateTree.Set(contractAddress, contract);
        stateTree.Set(TestItem.AddressB, plain);
        stateTree.Commit();

        RocksDbPersistence rocks = new(_db, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = rocks.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            batch.SetAccount(contractAddress, contract);
            batch.SetAccount(TestItem.AddressB, plain);
            foreach ((UInt256 slot, byte[] value) in slots) batch.SetStorage(contractAddress, slot, SlotValue.FromSpanWithoutLeadingZero(value));
        }

        ArenaBasePersistence arena = NewArenaPersistence();
        Assert.That(NewConverter(arena).Convert(CancellationToken.None), Is.True);

        using IPersistence.IPersistenceReader reader = arena.CreateReader();
        FlatTrieVerifier verifier = new(LimboLogs.Instance);
        verifier.Verify(reader, trieStore, stateTree.RootHash, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.Stats.AccountCount, Is.EqualTo(2));
            Assert.That(verifier.Stats.SlotCount, Is.EqualTo(slots.Length));
            Assert.That(verifier.Stats.MismatchedAccount, Is.Zero);
            Assert.That(verifier.Stats.MismatchedSlot, Is.Zero);
            Assert.That(verifier.Stats.MissingInFlat, Is.Zero);
            Assert.That(verifier.Stats.MissingInTrie, Is.Zero);
        }
    }

    [Test]
    public void Fold_IncrementsFoldMetric()
    {
        SeedViaRocks();
        ArenaBasePersistence arena = NewArenaPersistence();
        long before = Metrics.BaseStoreFolds;

        arena.Fold();

        Assert.That(Metrics.BaseStoreFolds, Is.EqualTo(before + 1));
    }

    [Test]
    public void Convert_EndToEnd_OnRealRocksDb()
    {
        using TempPath baseDbPath = TempPath.GetTempDirectory();
        (Address Address, Account Account)[] accounts = SeedAccounts;
        (Address Address, UInt256 Slot, byte[] Value)[] slots = SeedSlots;

        // Boot 1: write through the Rocks base store on a real RocksDB datadir.
        using (IContainer container = BuildContainer(baseDbPath.Path, new FlatDbConfig { Enabled = true }))
        {
            IPersistence persistence = container.Resolve<IPersistence>();
            using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis);
            foreach ((Address address, Account account) in accounts) batch.SetAccount(address, account);
            foreach ((Address address, UInt256 slot, byte[] value) in slots)
                batch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(value));
        }

        // Boot 2: Arena + ConvertBaseStore — run the conversion through the registered init step, the
        // same registration and instance the steps manager executes, then read back.
        using (IContainer container = BuildContainer(baseDbPath.Path,
            new FlatDbConfig { Enabled = true, BaseStore = FlatBaseStore.Arena, ConvertBaseStore = true }))
        {
            Assert.That(container.Resolve<IEnumerable<StepInfo>>().Select(static s => s.StepType),
                Does.Contain(typeof(ConvertFlatBaseStore)), "the conversion step must be registered when the flag is on");
            ConvertFlatBaseStore step = (ConvertFlatBaseStore)container.Resolve(typeof(ConvertFlatBaseStore));
            step.Execute(CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(container.Resolve<FlatBaseStoreConverter>().Convert(CancellationToken.None),
                Is.False, "idempotent within the same boot");

            IColumnsDb<FlatDbColumns> db = container.Resolve<IColumnsDb<FlatDbColumns>>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(db.GetColumnDb(FlatDbColumns.Account).GetAll().Count(), Is.Zero, "account overlay");
                Assert.That(db.GetColumnDb(FlatDbColumns.Storage).GetAll().Count(), Is.Zero, "storage overlay");
                Assert.That(ArenaBasePersistence.ReadBaseStoreKind(db), Is.EqualTo(FlatBaseStore.Arena));
            }

            AssertSeededStateReadable(container.Resolve<IPersistence>());
        }

        // Boot 3: Arena without the convert flag — the converted DB boots and reads normally, and the
        // conversion step is not even registered.
        using (IContainer container = BuildContainer(baseDbPath.Path,
            new FlatDbConfig { Enabled = true, BaseStore = FlatBaseStore.Arena }))
        {
            Assert.That(container.Resolve<IEnumerable<StepInfo>>().Select(static s => s.StepType),
                Does.Not.Contain(typeof(ConvertFlatBaseStore)));
            AssertSeededStateReadable(container.Resolve<IPersistence>());
        }

        // Boot 4: Rocks on the converted DB must fail loudly (Autofac wraps the resolve-time throw).
        using (IContainer container = BuildContainer(baseDbPath.Path, new FlatDbConfig { Enabled = true }))
        {
            Exception? thrown = null;
            try
            {
                container.Resolve<IPersistence>();
            }
            catch (Exception e)
            {
                thrown = e;
            }

            Exception? cause = thrown;
            while (cause is not null and not InvalidConfigurationException) cause = cause.InnerException;
            Assert.That(cause, Is.InstanceOf<InvalidConfigurationException>(), thrown?.ToString() ?? "resolve unexpectedly succeeded");
        }
    }

    private static IContainer BuildContainer(string baseDbPath, FlatDbConfig flatDbConfig) => new ContainerBuilder()
        .AddModule(new NethermindModule(
            new ChainSpec(),
            new ConfigProvider(flatDbConfig, new InitConfig { BaseDbPath = baseDbPath }),
            LimboLogs.Instance))
        .AddSingleton<IProcessExitSource>(Substitute.For<IProcessExitSource>())
        .Build();
}
