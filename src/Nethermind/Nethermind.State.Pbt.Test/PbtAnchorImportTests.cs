// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Steps;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using NUnit.Framework;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtAnchorImportTests
{
    [Test]
    public async Task Production_module_initializes_only_after_standard_flat_genesis([Values("normal", "wrong-genesis")] string mode)
    {
        ChainSpec chain = Eip8347FixtureState.LoadChainSpec();
        PbtConfig config = new() { Enabled = true, MigrationGenesisBootstrap = true };
        using TempPath scratch = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(new FlatDbConfig { Enabled = true },
                new Nethermind.Api.InitConfig { BaseDbPath = scratch.Path, GenesisHash = mode == "wrong-genesis" ? Hash256.Zero.ToString() : null }), chain, useTestSpecProvider: false))
            .AddSingleton<IPbtConfig>(config)
            .AddModule(new PbtMigrationModule(config))
            .Build();
        PbtMigrationBootstrap bootstrap = container.Resolve<PbtMigrationBootstrap>();
        IWorldStateManager manager = container.Resolve<IWorldStateManager>();
        IStateBoundary boundary = container.Resolve<IStateBoundary>();
        Nethermind.Synchronization.ParallelSync.IFullStateFinder finder = container.Resolve<Nethermind.Synchronization.ParallelSync.IFullStateFinder>();
        Nethermind.Synchronization.SnapSync.ISnapTrieFactory snap = container.Resolve<Nethermind.Synchronization.SnapSync.ISnapTrieFactory>();
        Nethermind.Synchronization.FastSync.ITreeSyncStore treeSync = container.Resolve<Nethermind.Synchronization.FastSync.ITreeSyncStore>();
        Nethermind.Synchronization.SnapSync.IBalHealing healing = container.Resolve<Nethermind.Synchronization.SnapSync.IBalHealing>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager, Is.TypeOf<MigrationWorldStateManager>());
            Assert.That(boundary.BestPersistedState, Is.Null);
            Assert.That(finder.FindBestFullState(), Is.Zero);
            Assert.That(healing.IsAvailable, Is.False);
            Assert.Throws<NotSupportedException>(() => snap.CreateStateTree());
            Assert.Throws<NotSupportedException>(() => treeSync.EnsureStorageEmpty(Hash256.Zero));
        }
        using ILifetimeScope genesisScope = container.BeginLifetimeScope(builder =>
            builder.AddSingleton<IWorldStateScopeProvider>(manager.GlobalWorldState));
        IWorldState state = genesisScope.Resolve<IWorldState>();
        Block genesis;
        using (state.BeginScope(null)) genesis = genesisScope.Resolve<IGenesisBuilder>().Build();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: [genesis]);
        Hash256 expectedShadowRoot = Eip8347FixtureState.PbtRoot("anchor");
        IMigrationTelemetry telemetry = container.Resolve<IMigrationTelemetry>();
        Assert.That(telemetry.GetShadowRoot(genesis.Hash!), Is.Null, "main processing does not write genesis into PBT");
        if (mode == "wrong-genesis")
        {
            Assert.ThrowsAsync<InvalidDataException>(() => bootstrap.Initialize(CancellationToken.None));
            return;
        }

        await bootstrap.Initialize(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.GlobalStateReader.HasStateForBlock(genesis.Header), Is.True);
            Assert.That(telemetry.GetShadowRoot(genesis.Hash!), Is.EqualTo(expectedShadowRoot));
            Assert.That(container.Resolve<IColumnsDb<PbtColumns>>().GetColumnDb(PbtColumns.Metadata).Get("migrationPreparedAnchor"u8), Is.Not.Null, "the verified image is recorded as the anchor's provenance");
        }
        // A restart with the same source takes the fast path over the persisted native state.
        manager.FlushCache(CancellationToken.None);
        await bootstrap.Initialize(CancellationToken.None);
    }

    [Test]
    public async Task Portable_image_publishes_native_state_and_matching_restart_takes_the_fast_path(
        [Values("anchor", "a1", "a2", "a3", "a4", "a5", "b2", "b3", "b4", "b5", "b6")] string name, [Values] bool tinyVerifyBuffer)
    {
        // Tiny buffers keep the verifier's reader pausing on full buckets while its workers sweep them, and spill many spool runs.
        using Harness harness = new(name, tinyVerifyBuffer
            ? new PbtConfig { MigrationVerifyBucketBytes = 4096, ExportSortBufferBytes = 1024, ImportConcurrency = 2 }
            : new PbtConfig());
        ValueHash256 root = await harness.Publish();
        AssertPublishedState(harness, root, name);
        harness.Reopen();
        harness.Target.OnSync = () => Assert.Fail("A matching restart must not rebuild the native anchor");

        ValueHash256 reused = await harness.Publish();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused, Is.EqualTo(root));
            Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Is.Empty);
        }
        AssertPublishedState(harness, reused, name);
    }

    /// <remarks>The anchor now comes only from the consumer's own chain, so the image's agreement with the
    /// anchor's MPT root is the whole of the check; there is no second description of the anchor to disagree.</remarks>
    [Test]
    public void Rejects_wrong_trusted_anchor(
        [Values("trusted-root", "activation", "missing-hash", "missing-root", "claimed-pbt-root")] string failure)
    {
        using Harness harness = new("anchor");
        switch (failure)
        {
            case "trusted-root": harness.Anchor = harness.WithStateRoot(Hash256.Zero); break;
            case "activation": harness.Anchor.Header.Timestamp = harness.Anchor.ActivationTimestamp!.Value; break;
            case "missing-hash": harness.Anchor.Header.Hash = null; break;
            case "missing-root": harness.Anchor.Header.StateRoot = null!; break;
        }
        byte[] bytes = File.ReadAllBytes(Eip8347FixtureState.ArtifactPath("anchor", "snapshot.pbt"));
        if (failure == "claimed-pbt-root") bytes[^1] ^= 1;
        using MemoryStream snapshot = new(bytes);
        using FileStream preimages = OpenArtifact("anchor", "preimages.bin");

        InvalidDataException exception = Assert.ThrowsAsync<InvalidDataException>(() => harness.Publish(snapshot, preimages, CancellationToken.None))!;

        if (failure == "claimed-pbt-root") Assert.That(exception.Message, Does.Contain("claimed root"));
        AssertUnpublished(harness);
    }

    /// <remarks>An anchor without an activation is an export on a chain specification that schedules no
    /// binaryTrieTime; there is then nothing for the anchor to precede.</remarks>
    [Test]
    public async Task Accepts_an_anchor_without_an_activation()
    {
        using Harness harness = new("anchor");
        harness.Anchor = harness.Anchor with { ActivationTimestamp = null };

        AssertPublishedState(harness, await harness.Publish(), "anchor");
    }

    [Test]
    public async Task Local_resource_refusal_does_not_invalidate_the_import_and_can_be_retried()
    {
        using Harness harness = new("a4");
        (MemoryStream snapshot, MemoryStream preimages) = Corrupt("a4", "oversized-code");
        using (snapshot)
        using (preimages)
            Assert.ThrowsAsync<PbtImageResourceLimitException>(() =>
                harness.Publish(snapshot, preimages, CancellationToken.None));

        AssertUnpublished(harness);
        AssertPublishedState(harness, await harness.Publish(), "a4");
    }

    /// <remarks>Each snapshot carries a recomputed PBT root, so only the staging checks stand between it and publication.</remarks>
    [Test]
    public void Noncanonical_snapshot_is_refused_with_or_without_preimages(
        [Values("code", "pushdata", "padding", "code-size", "missing-code", "delegation-with-code-leaves", "extra-code-chunk",
            "orphan-storage-leaf")] string corruption, [Values] bool withPreimages) =>
        AssertRefused(corruption, withPreimages);

    [Test]
    public void Preimages_refuse_a_snapshot_off_the_anchor_state(
        [Values("nonce", "balance", "storage", "missing-storage", "surplus-account-leaves", "anchored-elsewhere",
            "missing-account-preimage", "missing-slot-preimage", "wrong-account-preimage", "wrong-slot-preimage", "surplus-slot-preimage")] string corruption) =>
        AssertRefused(corruption, withPreimages: true);

    [TestCase("cancelled", typeof(OperationCanceledException))]
    [TestCase("cancelled-on-sync", typeof(OperationCanceledException))]
    [TestCase("sync-failure", typeof(IOException))]
    [TestCase("anchor-changed", typeof(InvalidOperationException))]
    public void Failed_publication_publishes_nothing(string failure, Type expected)
    {
        using Harness harness = new("a4");
        using CancellationTokenSource cancellation = new();
        switch (failure)
        {
            case "cancelled": cancellation.Cancel(); break;
            case "cancelled-on-sync": harness.Target.OnSync = cancellation.Cancel; break;
            case "sync-failure": harness.Target.OnSync = () => throw new IOException("Injected native WAL sync failure"); break;
            case "anchor-changed": harness.IsAnchorCurrent = () => false; break;
        }

        Assert.ThrowsAsync(Is.InstanceOf(expected), () => harness.Publish(cancellation.Token));

        AssertUnpublished(harness);
        harness.Reopen();
        AssertUnpublished(harness);
    }

    [Test]
    public async Task Interrupted_native_anchor_recovers_only_matching_prepared_source([Values] bool changeSource, [Values] bool duringStaging)
    {
        PbtConfig config = new() { MigrationSnapshotPath = "snapshot.pbt" };
        using Harness harness = new("a4", config);
        using CancellationTokenSource cancellation = new();
        harness.Target.OnSync = () =>
        {
            if (duringStaging || harness.Target.GetColumnDb(PbtColumns.Metadata).Get("validState"u8) is not null) cancellation.Cancel();
        };
        Assert.That(() => harness.Publish(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        harness.Target.OnSync = () => { };
        if (duringStaging)
        {
            // A crash mid-staging leaves staged rows without a validity marker.
            using IPbtPersistence.IWriteBatch batch = new PbtRocksDbPersistence(harness.Target, config, NullTrieNodeLog.Instance).CreateStagingWriteBatch(WriteFlags.None);
            batch.SetAccount(PbtStateKey.AddressKeyHash(TestItem.AddressA), Account.TotallyEmpty.ToPbtAccount());
            batch.Commit();
        }
        harness.Reopen();
        if (changeSource)
        {
            harness.Anchor = harness.Anchor with { ChainId = "another-chain" };
            Assert.ThrowsAsync<InvalidDataException>(() => harness.Publish());
            Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Is.Empty);
        }
        else AssertPublishedState(harness, await harness.Publish(), "a4");
    }

    [Test]
    public void Offline_export_publishes_verified_bundle_without_native_state([Values("success", "existing", "corrupt")] string mode, [Values] bool includePreimages)
    {
        using Harness harness = new("a5");
        using OfflineFixture offline = new("a5", harness.Anchor.Header);
        string output = Path.Combine(harness.Scratch.Path, "bundle");
        if (mode == "existing") Directory.CreateDirectory(output);
        if (mode == "corrupt") harness.Anchor = harness.WithStateRoot(Hash256.Zero);
        void Export() => PbtOfflineExport.Export(offline.Source, offline.Code, harness.Anchor,
            output, harness.Scratch.Path, harness.IsAnchorCurrent, includePreimages, sortBufferBytes: 65536, workerCount: 2,
            LimboLogs.Instance, CancellationToken.None);
        if (mode == "existing") Assert.Throws<IOException>(Export);
        else if (mode == "corrupt")
        {
            Assert.Throws<InvalidDataException>(Export);
            Assert.That(Directory.GetFiles(output), Has.Length.EqualTo(includePreimages ? 2 : 1));
        }
        else
        {
            Export();
            using FileStream expected = OpenArtifact("a5", "snapshot.pbt");
            using MemoryStream expectedBytes = new();
            expected.CopyTo(expectedBytes);
            Assert.That(File.ReadAllBytes(Path.Combine(output, "snapshot.pbt")), Is.EqualTo(expectedBytes.ToArray()));
            Assert.That(Directory.GetFiles(output), Has.Length.EqualTo(includePreimages ? 2 : 1));
        }
        Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Has.Length.EqualTo(1));
        AssertNoNativeState(harness);
    }

    [Test]
    public async Task Snapshot_alone_publishes_the_native_state([Values("anchor", "a1", "a2", "a3", "a4", "a5")] string name)
    {
        using Harness harness = new(name);
        using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
        ValueHash256 root = await harness.Publish(snapshot, null, CancellationToken.None);

        AssertPublishedState(harness, root, name);
        Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Is.Empty);
    }

    /// <remarks>Stages several times the staging batch size, so the writes span many batches written by parallel flushers.</remarks>
    [Test]
    public async Task Snapshot_staged_across_many_batches_publishes_every_account_and_slot([Values(0, 1)] int importConcurrency)
    {
        using Harness harness = new("anchor", new PbtConfig { ImportConcurrency = importConcurrency });
        Address[] addresses = new Address[1000];
        List<RebuildEntry> leaves = [];
        for (int index = 0; index < addresses.Length; index++)
        {
            Address address = addresses[index] = Address.FromNumber((UInt256)(index + 1));
            ValueHash256 basicData = default;
            PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, 0, (UInt256)(index + 1), UInt256.Zero);
            leaves.Add(new((PbtVariableTreeKey)PbtStateKey.Account(PbtStateKey.AddressKeyHash(address), 0), basicData));
            leaves.Add(new((PbtVariableTreeKey)PbtStateKey.Account(PbtStateKey.AddressKeyHash(address), 1), Keccak.OfAnEmptyString.ValueHash256));
            leaves.Add(new(PbtStateKey.Slot(address, 100), ((UInt256)(index + 1)).ToValueHash()));
        }
        leaves.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        ValueHash256 expectedRoot = PbtRightmostGroupStore.CalculateRoot(leaves, PbtRightmostGroupStore.DefaultWindowSize, Environment.ProcessorCount, CancellationToken.None);
        using MemoryStream snapshot = new();
        PbtSnapshotCodec.Write(snapshot, leaves, PbtTestLeaves.Claiming(expectedRoot));
        snapshot.Position = 0;

        ValueHash256 root = await harness.Publish(snapshot, null, CancellationToken.None);

        using IPbtPersistence.IReader reader = new PbtRocksDbPersistence(harness.Target, new PbtConfig(), NullTrieNodeLog.Instance).CreateReader();
        Assert.That(root, Is.EqualTo(expectedRoot));
        for (int index = 0; index < addresses.Length; index++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetAccount(PbtStateKey.AddressKeyHash(addresses[index]))?.ToAccount().Nonce, Is.EqualTo((ulong)(index + 1)));
                Assert.That(reader.GetSlot(PbtStateKey.Slot(addresses[index], 100)),
                    Is.EqualTo(EvmWordSlot.FromStripped(((UInt256)(index + 1)).ToBigEndian())));
            }
        }
    }

    // A command run is pruned to its dependency closure, so metrics only start if the command asks for them.
    [TestCase(typeof(ImportPbtFromPreimageFlat), new[] { typeof(InitializeBlockTree), typeof(StartMonitoring) }, new Type[0])]
    [TestCase(typeof(ScanPbtTree), new[] { typeof(InitializeBlockTree), typeof(StartMonitoring) }, new Type[0])]
    // An export anchor ahead of the persisted state is only reachable once the node syncs.
    [TestCase(typeof(ExportPbtImage), new[] { typeof(InitializeNetwork), typeof(StartMonitoring) }, new Type[0])]
    [TestCase(typeof(InitializePbtMigration), new[] { typeof(LoadGenesisBlock), typeof(StartMonitoring) }, new[] { typeof(InitializeNetwork) })]
    [TestCase(typeof(ImportMigrationSnapshotWithFakeRoots), new[] { typeof(LoadGenesisBlock), typeof(StartMonitoring) }, new[] { typeof(ReviewBlockTree), typeof(InitializeNetwork) })]
    public void Step_declares_its_runner_dependencies(Type step, Type[] dependencies, Type[] dependents)
    {
        RunnerStepDependenciesAttribute attribute = (RunnerStepDependenciesAttribute)Attribute.GetCustomAttribute(step, typeof(RunnerStepDependenciesAttribute))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.Dependencies, Is.SupersetOf(dependencies));
            Assert.That(attribute.Dependents, Is.SupersetOf(dependents));
        }
    }

    [Test]
    public async Task Fake_root_snapshot_import_switches_the_node_to_pbt_without_migrating([Values] bool withPreimages)
    {
        ChainSpec chain = Eip8347FixtureState.LoadChainSpec();
        chain.Parameters.Eip8347TransitionTimestamp = null;
        PbtConfig config = new()
        {
            Enabled = true,
            ImportMigrationSnapshotWithFakeRoots = true,
            MigrationAnchor = 0,
            MigrationSnapshotPath = Eip8347FixtureState.ArtifactPath("anchor", "snapshot.pbt"),
            MigrationPreimagesPath = withPreimages ? Eip8347FixtureState.ArtifactPath("anchor", "preimages.bin") : null,
        };
        using TempPath scratch = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(new Nethermind.Api.InitConfig { BaseDbPath = scratch.Path }), chain, useTestSpecProvider: false))
            .AddSingleton<IPbtConfig>(config)
            .AddModule(new PbtModule(config))
            .Build();
        Block genesis = Build.A.Block.Genesis.WithStateRoot(new Hash256(Eip8347FixtureState.Metadata("anchor").GetProperty("mptRoot").GetString()!)).TestObject;
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: [genesis]);

        ImportMigrationSnapshotWithFakeRoots step = container.Resolve<ImportMigrationSnapshotWithFakeRoots>();
        await step.Execute(CancellationToken.None);
        // A restart with the same snapshot reuses the import.
        await step.Execute(CancellationToken.None);

        using IPbtPersistence.IReader reader = container.Resolve<IPbtPersistence>().CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IPbtChildHeaderSource>(), Is.TypeOf<PbtBlockTreeChildHeaderSource>());
            Assert.That(container.Resolve<IWorldStateManager>().GlobalStateReader.HasStateForBlock(genesis.Header), Is.True);
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(genesis.Header)));
            Assert.That(reader.CurrentRoot, Is.EqualTo(Eip8347FixtureState.PbtRoot("anchor").ValueHash256));
        }
    }

    private static void AssertRefused(string corruption, bool withPreimages)
    {
        using Harness harness = new("a4");
        (MemoryStream snapshot, MemoryStream preimages) = Corrupt("a4", corruption);
        using (snapshot)
        using (preimages)
        {
            InvalidDataException exception = Assert.ThrowsAsync<InvalidDataException>(() =>
                harness.Publish(snapshot, withPreimages ? preimages : null, CancellationToken.None))!;
            Assert.That(exception.Message, Does.Not.Contain("claimed root"));
        }
        AssertUnpublished(harness);
    }

    /// <summary>Corrupts one fixture's artifacts, recomputing the snapshot's claimed PBT root over the corrupted leaves.</summary>
    private static (MemoryStream Snapshot, MemoryStream Preimages) Corrupt(string name, string corruption)
    {
        using FileStream original = OpenArtifact(name, "snapshot.pbt");
        List<RebuildEntry> leaves = [.. PbtSnapshotCodec.ReadLeaves(original)];
        using FileStream originalPreimages = OpenArtifact(name, "preimages.bin");
        List<PbtAccountPreimages> accounts = PbtTestLeaves.ReadPreimages(originalPreimages);
        Address writer = new("0x1000000000000000000000000000000000000001");
        Address authority = new("0x2b5ad5c4795c026514f8317c7a215e218dccd6cf");
        Address history = new("0x0000f90827f1c53a10cb7a02335b175320002935");
        ValueHash256 writerCodeHash = ValueKeccak.Compute(Bytes.FromHexString("60003560005500"));
        PbtVariableTreeKey basic = (PbtVariableTreeKey)PbtStateKey.Account(PbtStateKey.AddressKeyHash(writer), 0);
        PbtVariableTreeKey chunk = (PbtVariableTreeKey)PbtStateKey.Code(writerCodeHash, 0);
        PbtVariableTreeKey delegation = (PbtVariableTreeKey)PbtStateKey.Account(PbtStateKey.AddressKeyHash(authority), 2);
        PbtVariableTreeKey storageKey = PbtStateKey.Slot(history, UInt256.Zero);
        switch (corruption)
        {
            case "code": Mutate(chunk, 1); break;
            case "pushdata": Mutate(chunk, 0); break;
            case "padding": Mutate(chunk, 31); break;
            case "code-size": Mutate(basic, 7); break;
            // A claimed code size of 2^28 plus the real one, whose buffering exceeds the local budget.
            case "oversized-code":
                int basicIndex = leaves.FindIndex(entry => entry.Key.Equals(basic));
                byte[] oversized = leaves[basicIndex].Leaf.Bytes.ToArray();
                oversized[4] = 0x10;
                leaves[basicIndex] = new(basic, new ValueHash256(oversized));
                break;
            case "nonce": Mutate(basic, 15); break;
            case "balance": Mutate(basic, 31); break;
            case "storage": Mutate(storageKey, 31); break;
            case "missing-storage": Remove(storageKey); break;
            case "missing-code": Remove(chunk); break;
            case "delegation-with-code-leaves":
                byte[] delegationCode = leaves.Find(entry => entry.Key.Equals(delegation)).Leaf.Bytes[..23].ToArray();
                leaves.Add(new((PbtVariableTreeKey)PbtStateKey.Code(ValueKeccak.Compute(delegationCode), 0), new ValueHash256(PbtTreeHarness.ChunkifyCode(delegationCode))));
                break;
            // A chunk past the account's code size: reachable by no code read, so nothing accounts for it.
            case "extra-code-chunk": leaves.Add(new((PbtVariableTreeKey)PbtStateKey.Code(writerCodeHash, 1), Keccak.OfAnEmptyString.ValueHash256)); break;
            case "surplus-account-leaves":
                Address surplus = new("0x00000000000000000000000000000000deadbeef");
                ValueHash256 basicData = default;
                PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, 0, 1, UInt256.Zero);
                leaves.Add(new((PbtVariableTreeKey)PbtStateKey.Account(PbtStateKey.AddressKeyHash(surplus), 0), basicData));
                leaves.Add(new((PbtVariableTreeKey)PbtStateKey.Account(PbtStateKey.AddressKeyHash(surplus), 1), Keccak.OfAnEmptyString.ValueHash256));
                break;
            case "orphan-storage-leaf":
                leaves.Add(new(PbtStateKey.Slot(new Address("0x00000000000000000000000000000000cafebabe"), 100),
                    new ValueHash256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001"))));
                break;
            // Both artifacts drop one account consistently: the image is whole, but not the anchor's state.
            case "anchored-elsewhere":
                Address dropped = Eoa();
                ValueHash256 droppedStem = PbtStateKey.AddressKeyHash(dropped);
                leaves.RemoveAll(entry => entry.Key.Bytes[0] == 0 && entry.Key.Bytes.Slice(1, 32).SequenceEqual(droppedStem.Bytes));
                accounts.RemoveAll(account => account.Address == dropped);
                break;
            case "missing-account-preimage": accounts.RemoveAt(accounts.FindIndex(account => account.Address == writer)); break;
            case "missing-slot-preimage": ChangeSlots(slots => slots.RemoveAt(0)); break;
            case "wrong-slot-preimage": ChangeSlots(slots => slots[0] = Keccak.OfAnEmptyString.ValueHash256); break;
            case "surplus-slot-preimage": ChangeSlots(slots => slots.Add(Keccak.OfAnEmptyString.ValueHash256)); break;
            case "wrong-account-preimage":
                int accountIndex = accounts.FindIndex(account => account.Address == writer);
                accounts[accountIndex] = accounts[accountIndex] with { Address = new Address("0x9999999999999999999999999999999999999999") };
                accounts.Sort(static (left, right) => CompareHashes(ValueKeccak.Compute(left.Address.Bytes), ValueKeccak.Compute(right.Address.Bytes)));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        leaves.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        ValueHash256 attackerRoot = PbtRightmostGroupStore.CalculateRoot(leaves, PbtRightmostGroupStore.DefaultWindowSize, Environment.ProcessorCount, CancellationToken.None);
        MemoryStream snapshot = new();
        PbtSnapshotCodec.Write(snapshot, leaves, PbtTestLeaves.Claiming(attackerRoot));
        snapshot.Position = 0;
        MemoryStream preimages = new();
        PbtPreimageCodec.Write(preimages, accounts);
        preimages.Position = 0;
        return (snapshot, preimages);

        void Mutate(PbtVariableTreeKey key, int offset)
        {
            int index = leaves.FindIndex(entry => entry.Key.Equals(key));
            Assert.That(index, Is.GreaterThanOrEqualTo(0), corruption);
            byte[] bytes = leaves[index].Leaf.Bytes.ToArray();
            bytes[offset] ^= 1;
            leaves[index] = new(key, new ValueHash256(bytes));
        }

        void Remove(PbtVariableTreeKey key) => Assert.That(leaves.RemoveAll(entry => entry.Key.Equals(key)), Is.EqualTo(1));

        // A codeless account without storage, whose leaves are only its basic data and empty code hash.
        Address Eoa() => accounts.Find(account => account.SlotCount == 0 && leaves.Exists(entry =>
            entry.Key.Equals((PbtVariableTreeKey)PbtStateKey.Account(PbtStateKey.AddressKeyHash(account.Address), 1)) && entry.Leaf == Keccak.OfAnEmptyString.ValueHash256)).Address;

        void ChangeSlots(Action<List<ValueHash256>> change)
        {
            int index = accounts.FindIndex(account => account.Address == history);
            List<ValueHash256> slots = [.. accounts[index].Slots];
            change(slots);
            slots.Sort(static (left, right) => CompareHashes(ValueKeccak.Compute(left.Bytes), ValueKeccak.Compute(right.Bytes)));
            accounts[index] = new(history, (uint)slots.Count, slots);
        }
    }

    private static int CompareHashes(ValueHash256 left, ValueHash256 right) => left.Bytes.SequenceCompareTo(right.Bytes);

    private static void AssertUnpublished(Harness harness)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Pbt.Manager.HasStateForBlock(new StateId(harness.Anchor.Header)), Is.False);
            Assert.That(harness.Target.GetColumnDb(PbtColumns.Metadata).Get("validState"u8), Is.Null);
            Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Is.Empty);
        }
    }

    /// <remarks>Opening the persistence stamps the schema epoch; nothing else may be there.</remarks>
    private static void AssertNoNativeState(Harness harness)
    {
        foreach (PbtColumns column in harness.Target.ColumnKeys)
        {
            IEnumerable<byte[]> keys = harness.Target.GetColumnDb(column).GetAllKeys();
            if (column == PbtColumns.Metadata) Assert.That(keys, Is.EquivalentTo(new[] { "schemaEpoch"u8.ToArray() }));
            else Assert.That(keys, Is.Empty, column.ToString());
        }
    }

    private static void AssertPublishedState(Harness harness, ValueHash256 root, string name)
    {
        PbtConfig config = new();
        PbtRocksDbPersistence reopened = new(harness.Target, config, NullTrieNodeLog.Instance);
        using IPbtPersistence.IReader reader = reopened.CreateReader();
        Hash256 expectedRoot = Eip8347FixtureState.PbtRoot(name);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expectedRoot.ValueHash256));
            Assert.That(reader.CurrentRoot, Is.EqualTo(expectedRoot.ValueHash256));
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(harness.Anchor.Header)));
            Assert.That(harness.Pbt.Manager.HasStateForBlock(new StateId(harness.Anchor.Header)), Is.True, "the live manager sees the imported anchor");
        }
        foreach ((Address address, GethGenesisAllocJson expected) in Eip8347FixtureState.LoadAllocation(Eip8347FixtureState.Directory, name))
        {
            Account? account = reader.GetAccount(PbtStateKey.AddressKeyHash(address))?.ToAccount();
            Assert.That(account, Is.Not.Null, address.ToString());
            byte[] code = expected.Code ?? [];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(account!.Nonce, Is.EqualTo(expected.Nonce ?? 0), address.ToString());
                Assert.That(account.Balance, Is.EqualTo(expected.Balance), address.ToString());
                Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(code)), address.ToString());
                if (code.Length != 0) Assert.That(reader.GetCode(account.CodeHash.ValueHash256)?.Code.ToArray(), Is.EqualTo(code), address.ToString());
            }
            if (expected.Storage is null) continue;
            foreach ((UInt256 slot, byte[] value) in expected.Storage)
                Assert.That(reader.GetSlot(PbtStateKey.Slot(address, slot)),
                    Is.EqualTo(EvmWordSlot.FromStripped(new UInt256(value, isBigEndian: true).ToBigEndian())), slot.ToString());
        }

        using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
        using PbtNodeGroupStore oracle = new();
        List<(byte[] Key, byte[]? Value)> leaves = [];
        foreach (RebuildEntry leaf in PbtSnapshotCodec.ReadLeaves(snapshot)) leaves.Add((leaf.Key.Bytes.ToArray(), leaf.Leaf.ToByteArray()));
        Assert.That(oracle.Fold(default, leaves, PbtTreeHarness.DefaultFanOut, null), Is.EqualTo(expectedRoot.ValueHash256));
        PbtStoreTestExtensions.AssertSameGroups(harness.Target, reader, oracle);
    }

    private static FileStream OpenArtifact(string name, string file) => File.OpenRead(Eip8347FixtureState.ArtifactPath(name, file));

    private sealed class Harness : IDisposable
    {
        public readonly TempPath Scratch = TempPath.GetTempDirectory();
        public readonly SyncColumnsDb Target = new();
        public PbtImageAnchor Anchor;
        public Func<bool> IsAnchorCurrent = () => true;
        public PbtTestContext Pbt { get; private set; } = null!;
        private PbtAnchorImport? _anchorImport;
        public PbtAnchorImport AnchorImport => _anchorImport ??=
            new PbtAnchorImport(new PbtRocksDbPersistence(Target, _config, NullTrieNodeLog.Instance), Target, Pbt.Coordinator, _config, LimboLogs.Instance);
        private readonly string _name;
        private readonly PbtConfig _config;

        public Harness(string name) : this(name, new PbtConfig()) { }

        public Harness(string name, PbtConfig config)
        {
            _name = name;
            _config = config;
            Directory.CreateDirectory(Scratch.Path);
            Anchor = new("1", new Hash256(Eip8347FixtureState.Metadata("anchor").GetProperty("blockHash").GetString()!), Eip8347FixtureState.AnchorHeader(name), 48);
            Open();
        }

        public PbtImageAnchor WithStateRoot(Hash256 stateRoot) => Anchor with
        {
            Header = Build.A.BlockHeader.WithNumber(Anchor.Header.Number).WithTimestamp(0).WithStateRoot(stateRoot).TestObject
        };

        /// <summary>Reopens the native PBT stack over the same database, as a restart would.</summary>
        public void Reopen()
        {
            Pbt.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Open();
        }

        private void Open()
        {
            Pbt = new PbtTestContext(Target, _config);
            _anchorImport = null;
        }

        public async Task<ValueHash256> Publish(CancellationToken cancellationToken = default)
        {
            using FileStream snapshot = OpenArtifact(_name, "snapshot.pbt");
            using FileStream preimages = OpenArtifact(_name, "preimages.bin");
            return await Publish(snapshot, preimages, cancellationToken);
        }

        public Task<ValueHash256> Publish(Stream snapshot, Stream? preimages, CancellationToken cancellationToken) =>
            AnchorImport.ImportSnapshot(snapshot, preimages, Anchor, Scratch.Path, IsAnchorCurrent, cancellationToken);

        public void Dispose()
        {
            Pbt.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Target.Dispose();
            Scratch.Dispose();
        }
    }

    private sealed class OfflineFixture : IDisposable
    {
        private readonly SnapshotableMemColumnsDb<FlatDbColumns> _sourceDatabase = new();
        private readonly MemDb _code = new();
        public IPersistence.IPersistenceReader Source { get; }
        public IReadOnlyKeyValueStore Code => _code;

        public OfflineFixture(string name, BlockHeader anchorHeader)
        {
            PreimageRocksdbPersistence source = new(_sourceDatabase, LimboLogs.Instance, FlatLayout.PreimageFlat);
            using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
            using FileStream preimages = OpenArtifact(name, "preimages.bin");
            Eip8347FixtureState.ReplayInto(source, _code, anchorHeader, snapshot, preimages);
            Source = source.CreateReader();
        }

        public void Dispose()
        {
            Source.Dispose();
            _code.Dispose();
            _sourceDatabase.Dispose();
        }
    }

    private sealed class SyncColumnsDb : IColumnsDb<PbtColumns>
    {
        private readonly SnapshotableMemColumnsDb<PbtColumns> _database = new();
        public Action OnSync = () => { };
        public IDb GetColumnDb(PbtColumns key) => new SyncDb(_database.GetColumnDb(key), () => OnSync());
        public IEnumerable<PbtColumns> ColumnKeys => _database.ColumnKeys;
        public IColumnsWriteBatch<PbtColumns> StartWriteBatch() => _database.StartWriteBatch();
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot() => _database.CreateSnapshot();
        public void Flush(bool onlyWal = false) => _database.Flush(onlyWal);
        public void Dispose() => _database.Dispose();
    }

    private sealed class SyncDb(IDb database, Action sync) : IDb, ISortedKeyValueStore, IRangeRemovableKeyValueStore
    {
        public byte[]? FirstKey => ((ISortedKeyValueStore)database).FirstKey;
        public byte[]? LastKey => ((ISortedKeyValueStore)database).LastKey;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            ((ISortedKeyValueStore)database).GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags);
        public string Name => database.Name;
        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => database.Get(key, flags);
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => database.Set(key, value, flags);
        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => database[keys];
        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => database.GetAll(ordered);
        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => database.GetAllKeys(ordered);
        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => database.GetAllValues(ordered);
        public IWriteBatch StartWriteBatch() => database.StartWriteBatch();
        public void Flush(bool onlyWal = false) => database.Flush(onlyWal);
        public void SyncWal() => sync();
        public void Dispose() { }
        public void RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) => ((IRangeRemovableKeyValueStore)database).RemoveRange(firstKeyInclusive, lastKeyExclusive);
        public void ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) => ((IRangeRemovableKeyValueStore)database).ReclaimRange(firstKeyInclusive, lastKeyExclusive);
    }
}
