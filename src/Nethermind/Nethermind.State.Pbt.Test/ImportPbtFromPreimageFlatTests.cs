// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;
using NUnit.Framework;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Test;

public class ImportPbtFromPreimageFlatTests
{
    private const ulong SourceBlock = 7;

    /// <summary>Header root that import must use as the resulting state's key.</summary>
    /// <remarks>It is unrelated to fixture tree roots to prevent accidental matches.</remarks>
    private static readonly Hash256 SourceStateRoot = TestItem.KeccakA;

    // Zero uses the built-in window; small values force multiple windows with the same root.
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public async Task Imports_preimage_flat_state_into_pbt_and_exits(int windowSize)
    {
        PbtConfig config = new() { ImportWindowSize = windowSize };

        // More than 128 chunks exercises the overflow-code zone end-to-end.
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);      // header-region slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);     // storage-zone slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        // A second contract with the same code exercises content-addressed overflow-chunk deduplication.
        PbtReferenceModel.SetAccount(model, TestItem.AddressC, 9, 5, bigCode);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            // A non-empty flat storage root triggers storage fan-out; PBT omits it.
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedCodeHash(bigCodeHash).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 70, SlotValue.FromSpanWithoutLeadingZero([0x07]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
            batch.SetAccount(TestItem.AddressC, new Account(9, 5).WithChangedCodeHash(bigCodeHash));
        }

        MemDb codeDb = new();
        codeDb[bigCodeHash.Bytes] = bigCode;

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        // Both phases need the same column database; otherwise phase two scans nothing.
        ImportPbtFromPreimageFlat step = new(flatSource, codeDb, pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)), "the state is keyed by the source's header root");
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)), "with the folded tree's own root recorded beside it");
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)100));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressB)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressC)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(((UInt256)0x1234).ToBigEndian()));
    }

    /// <summary>Verifies merge-joining header-only storage and accounts without storage or code.</summary>
    [Test]
    public async Task Imports_accounts_with_only_header_storage_and_with_none()
    {
        PbtConfig config = new();

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 2, 200);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 0, 0x11);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 63, 0x22);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(2, 200).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 0, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(TestItem.AddressB, 63, SlotValue.FromSpanWithoutLeadingZero([0x22]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    /// <summary>
    /// Flat storage keys interleave accounts sharing their leading four address bytes, so copied slots
    /// must remain associated with their originating account.
    /// </summary>
    [Test]
    public async Task Imports_slots_of_accounts_sharing_a_storage_key_prefix()
    {
        PbtConfig config = new();

        // Equal leading address bytes cause their flat-storage slots to interleave.
        Address first = new(Bytes.FromHexString("0x00000000000000000000000000000000000000aa"));
        Address second = new(Bytes.FromHexString("0x00000000000000000000000000000000000000bb"));

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, first, 1, 100);
        PbtReferenceModel.SetAccount(model, second, 2, 200);
        PbtReferenceModel.SetSlot(model, first, 1, 0x11);      // header-region slot
        PbtReferenceModel.SetSlot(model, first, 1000, 0x22);   // storage-zone slot
        PbtReferenceModel.SetSlot(model, second, 1, 0x33);
        PbtReferenceModel.SetSlot(model, second, 1000, 0x44);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(first, new Account(1, 100).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetAccount(second, new Account(2, 200).WithChangedStorageRoot(TestItem.KeccakB));
            batch.SetStorage(first, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(first, 1000, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            batch.SetStorage(second, 1, SlotValue.FromSpanWithoutLeadingZero([0x33]));
            batch.SetStorage(second, 1000, SlotValue.FromSpanWithoutLeadingZero([0x44]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, first, 1000)).ToArray(), Is.EqualTo(((UInt256)0x22).ToBigEndian()));
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, second, 1000)).ToArray(), Is.EqualTo(((UInt256)0x44).ToBigEndian()));
    }

    /// <summary>
    /// A retry after a pre-pointer crash must reproduce the root without reading stale folded blobs.
    /// </summary>
    /// <param name="clearKeyChunk">A value of 1 reopens the view after each deleted key, verifying the exclusive resume cursor.</param>
    [TestCase(10_000)]
    [TestCase(1)]
    public async Task Rerunning_after_an_interrupted_import_reproduces_the_root(int clearKeyChunk)
    {
        PbtConfig config = new();

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());

        async Task<ValueHash256> Import()
        {
            RecordingExitSource exitSource = new();
            ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance)
            {
                ClearKeyChunk = clearKeyChunk,
            };
            await step.Execute(CancellationToken.None);
            Assert.That(exitSource.ExitCode, Is.EqualTo(0));

            using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
            return reader.CurrentRoot;
        }

        ValueHash256 first = await Import();
        Assert.That(first, Is.EqualTo(PbtReferenceModel.Root(model)));

        // Rewind only the pointer, retaining copied rows, blobs, and nodes.
        pbtDb.GetColumnDb(PbtColumns.Metadata).Remove("currentState"u8);

        Assert.That(await Import(), Is.EqualTo(first), "a restart over an interrupted import must reproduce the same root");
    }

    [Test]
    public async Task Consecutive_storage_slots_share_one_stem()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100).WithChangedStorageRoot(TestItem.KeccakA));
            // Slots 100 and 101 share a storage stem.
            batch.SetStorage(TestItem.AddressA, 100, SlotValue.FromSpanWithoutLeadingZero([0xAA]));
            batch.SetStorage(TestItem.AddressA, 101, SlotValue.FromSpanWithoutLeadingZero([0xBB]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        // A flush interval of 1 exercises same-stem merging across windows.
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, new PbtConfig()) { FlushEntryInterval = 1 }, pbtTarget, new PbtConfig(), exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);
        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        PbtScanReport report = await new PbtScanner(pbtDb, new PbtConfig(), LimboLogs.Instance).Scan(CancellationToken.None);
        Assert.That(report.LeafCount, Is.EqualTo(4), "two account leaves and two storage leaves must be present");
        Assert.That(report.InvalidLeafCount, Is.Zero);
    }

    /// <summary>
    /// Reopening leaf-column views mid-zone must resume without gaps or duplicates across all zones.
    /// </summary>
    /// <param name="viewLeafChunk">Number of stems read before reopening the view.</param>
    [TestCase(1)]
    [TestCase(5)]
    public async Task Reopening_the_leaf_view_mid_zone_folds_to_the_same_root(int viewLeafChunk)
    {
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42, bigCode);
        PbtReferenceModel.SetAccount(model, TestItem.AddressC, 9, 5, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        PbtReferenceModel.SetSlot(model, TestItem.AddressC, 2000, 0x55);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedCodeHash(bigCodeHash).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 70, SlotValue.FromSpanWithoutLeadingZero([0x07]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
            batch.SetAccount(TestItem.AddressC, new Account(9, 5).WithChangedCodeHash(bigCodeHash).WithChangedStorageRoot(TestItem.KeccakB));
            batch.SetStorage(TestItem.AddressC, 2000, SlotValue.FromSpanWithoutLeadingZero([0x55]));
        }

        MemDb codeDb = new();
        codeDb[bigCodeHash.Bytes] = bigCode;

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, codeDb, pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, new PbtConfig()) { FlushEntryInterval = 1 }, pbtTarget, new PbtConfig(), exitSource, LimboLogs.Instance)
        {
            ViewLeafChunk = viewLeafChunk,
        };

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)), "reopening the view mid-zone must fold to the same root");
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(((UInt256)0x1234).ToBigEndian()));
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, TestItem.AddressC, 2000)).ToArray(), Is.EqualTo(((UInt256)0x55).ToBigEndian()));
    }

    [Test]
    public async Task Importer_bypasses_cached_persistence_wrapper()
    {
        PbtConfig config = new();
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence rawPersistence = new(pbtDb, config);
        IPbtPersistence cachedWrapper = NSubstitute.Substitute.For<IPbtPersistence>();
        RecordingExitSource exitSource = new();

        ContainerBuilder builder = new();
        builder
            .AddSingleton<IPersistence>(flatSource)
            .AddKeyedSingleton<IDb>(DbNames.Code, new MemDb())
            .AddSingleton<IColumnsDb<PbtColumns>>(pbtDb)
            .AddSingleton<PbtRocksDbPersistence>(rawPersistence)
            .AddSingleton<IPbtPersistence>(rawPersistence)
            .AddDecorator<IPbtPersistence>((_, _) => cachedWrapper)
            .AddSingleton<IPbtConfig>(config)
            .AddSingleton<IProcessExitSource>(exitSource)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<PbtRebuilder>();
        builder.RegisterType<ImportPbtFromPreimageFlat>().WithAttributeFiltering();
        using IContainer container = builder.Build();

        ImportPbtFromPreimageFlat step = container.Resolve<ImportPbtFromPreimageFlat>();
        Assert.That(container.Resolve<IPbtPersistence>(), Is.SameAs(cachedWrapper));

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = rawPersistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
    }

    [Test]
    public async Task Skips_when_pbt_already_populated()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
        }

        // Ensure the persisted target state is not pre-genesis.
        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        ValueHash256 existingRoot = new(Keccak.Compute("existing").Bytes);
        using (pbtTarget.CreateWriteBatch(StateId.PreGenesis, new StateId(1, existingRoot), default, WriteFlags.None)) { }

        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, new PbtConfig()), pbtTarget, new PbtConfig(), exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.Null, "an already-populated target is skipped without exiting");
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(1, existingRoot)), "the existing state is left untouched");
    }

    private sealed class RecordingExitSource : IProcessExitSource
    {
        public int? ExitCode { get; private set; }
        public CancellationToken Token => CancellationToken.None;
        public void Exit(int exitCode) => ExitCode ??= exitCode;
    }
}
