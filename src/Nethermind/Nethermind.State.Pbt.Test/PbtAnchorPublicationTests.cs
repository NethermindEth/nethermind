// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
using FlatStateId = Nethermind.State.Flat.StateId;
using StateId = Nethermind.State.Pbt.StateId;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtAnchorPublicationTests
{
    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");

    [Test]
    public async Task Production_module_initializes_only_after_standard_flat_genesis([Values("normal", "wrong-genesis")] string mode)
    {
        using Stream input = typeof(PbtAnchorPublicationTests).Assembly.GetManifestResourceStream("Nethermind.State.Pbt.Test.Fixtures.Eip8347.genesis.json")!;
        ChainSpec chain = new GethGenesisLoader(new EthereumJsonSerializer()).Load(input);
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
        Hash256 expectedShadowRoot = new(Metadata("anchor").GetProperty("pbtRoot").GetString()!);
        Nethermind.JsonRpc.Modules.DebugModule.IMigrationTelemetry telemetry = container.Resolve<Nethermind.JsonRpc.Modules.DebugModule.IMigrationTelemetry>();
        Assert.That(telemetry.GetShadowRoot(genesis.Hash!), Is.EqualTo(expectedShadowRoot), "genesis is mirrored into PBT by main processing");
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
        [Values("anchor", "a1", "a2", "a3", "a4", "a5", "b2", "b3", "b4", "b5", "b6")] string name)
    {
        using Harness harness = new(name);
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
    [TestCase("trusted-root")]
    [TestCase("activation")]
    [TestCase("missing-hash")]
    [TestCase("missing-root")]
    [TestCase("claimed-pbt-root")]
    public void Rejects_wrong_trusted_anchor(string failure)
    {
        using Harness harness = new("anchor");
        switch (failure)
        {
            case "trusted-root": harness.Anchor = harness.WithStateRoot(Hash256.Zero); break;
            case "activation": harness.Anchor.Header.Timestamp = harness.Anchor.ActivationTimestamp!.Value; break;
            case "missing-hash": harness.Anchor.Header.Hash = null; break;
            case "missing-root": harness.Anchor.Header.StateRoot = null!; break;
        }
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures, "canonical", "anchor", "snapshot.pbt"));
        if (failure == "claimed-pbt-root") bytes[0] ^= 1;
        using MemoryStream snapshot = new(bytes);
        using FileStream preimages = OpenArtifact("anchor", "preimages.bin");

        Assert.ThrowsAsync<InvalidDataException>(() => harness.Publication.PublishSnapshot(snapshot, preimages, harness.Anchor, harness.Scratch.Path, () => true));

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
        using Harness harness = new("a5");
        PbtImageAnchor anchor = harness.Anchor;
        harness.Anchor = anchor with { MaxBufferedCodeBytes = 0 };

        Assert.ThrowsAsync<PbtImageResourceLimitException>(() => harness.Publish());

        AssertUnpublished(harness);
        harness.Anchor = anchor;
        AssertPublishedState(harness, await harness.Publish(), "a5");
    }

    /// <remarks>Each snapshot carries a recomputed PBT root, so only the staging checks stand between it and publication.</remarks>
    [Test]
    public void Noncanonical_snapshot_is_refused_with_or_without_preimages(
        [Values("code", "pushdata", "padding", "code-size", "version", "reserved", "missing-basic", "missing-code-hash", "missing-code",
            "delegation-prefix", "delegation-padding", "delegation-size", "delegation-code-hash", "delegation-with-code-leaves",
            "reserved-sub-index", "extra-code-chunk", "codeless-without-code-hash")] string corruption, [Values] bool withPreimages) =>
        AssertRefused(corruption, withPreimages);

    [Test]
    public void Preimages_refuse_a_snapshot_off_the_anchor_state(
        [Values("nonce", "balance", "storage", "missing-storage", "surplus-account-leaves", "orphan-storage-leaf", "anchored-elsewhere",
            "missing-account-preimage", "missing-slot-preimage", "wrong-account-preimage", "wrong-slot-preimage", "surplus-slot-preimage")] string corruption) =>
        AssertRefused(corruption, withPreimages: true);

    [Test]
    public void Cancellation_does_not_publish([Values] bool afterNativeSync)
    {
        using Harness harness = new("a4");
        using CancellationTokenSource cancellation = new();
        if (afterNativeSync) harness.Target.OnSync = cancellation.Cancel;
        else cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => harness.Publish(cancellation.Token));

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
        Assert.ThrowsAsync<OperationCanceledException>(() => harness.Publish(cancellation.Token));
        harness.Target.OnSync = () => { };
        if (duringStaging)
        {
            // A crash mid-staging leaves staged rows without a validity marker.
            using IPbtPersistence.IWriteBatch batch = new PbtRocksDbPersistence(harness.Target, config).CreateStagingWriteBatch(WriteFlags.None);
            batch.SetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), Account.TotallyEmpty);
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
    public void Native_sync_failure_does_not_publish_catalog()
    {
        using Harness harness = new("a4");
        harness.Target.OnSync = () => throw new IOException("Injected native WAL sync failure");

        Assert.ThrowsAsync<IOException>(() => harness.Publish());

        AssertUnpublished(harness);
        harness.Reopen();
        AssertUnpublished(harness);
    }

    [Test]
    public void Changed_anchor_before_publication_does_not_publish()
    {
        using Harness harness = new("anchor");
        harness.IsAnchorCurrent = () => false;

        Assert.ThrowsAsync<InvalidOperationException>(() => harness.Publish());

        AssertUnpublished(harness);
    }

    [Test]
    public void Offline_export_publishes_verified_bundle_without_native_state([Values("success", "existing", "corrupt")] string mode)
    {
        using Harness harness = new("a5");
        using BootstrapLease lease = new(harness, "a5", offline: true);
        string output = Path.Combine(harness.Scratch.Path, "bundle");
        if (mode == "existing") Directory.CreateDirectory(output);
        if (mode == "corrupt") harness.Anchor = harness.WithStateRoot(Hash256.Zero);
        void Export() => PbtOfflineExport.Export(lease.OfflineSource!, lease.OfflineCode!, harness.Anchor,
            output, harness.Scratch.Path, harness.IsAnchorCurrent, sortBufferBytes: 65536, workerCount: 2,
            LimboLogs.Instance, CancellationToken.None);
        if (mode == "existing") Assert.Throws<IOException>(Export);
        else if (mode == "corrupt")
        {
            Assert.Throws<InvalidDataException>(Export);
            Assert.That(Directory.Exists(output), Is.False);
        }
        else
        {
            Export();
            using FileStream expected = OpenArtifact("a5", "snapshot.pbt");
            using MemoryStream expectedBytes = new();
            expected.CopyTo(expectedBytes);
            Assert.That(File.ReadAllBytes(Path.Combine(output, "snapshot.pbt")), Is.EqualTo(expectedBytes.ToArray()));
            Assert.That(Directory.GetFiles(output), Has.Length.EqualTo(2));
        }
        Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Has.Length.EqualTo(mode == "corrupt" ? 0 : 1));
        AssertNoNativeState(harness);
    }

    [Test]
    public async Task Offline_source_publishes_the_same_native_state_as_the_portable_image()
    {
        using Harness harness = new("a5");
        using BootstrapLease lease = new(harness, "a5", offline: true);
        string directory = Path.Combine(harness.Scratch.Path, "offline");
        Directory.CreateDirectory(directory);
        await using FileStream exportedSnapshot = new(Path.Combine(directory, "snapshot.pbt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await using FileStream exportedPreimages = new(Path.Combine(directory, "preimages.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        PbtOfflineSource.WriteArtifacts(lease.OfflineSource!, lease.OfflineCode!, lease.Anchor, directory,
            exportedSnapshot, exportedPreimages, LimboLogs.Instance, sortBufferBytes: 65536, workerCount: 2, CancellationToken.None);
        exportedSnapshot.Position = 0;
        exportedPreimages.Position = 0;

        ValueHash256 root = await harness.Publication.PublishSnapshot(exportedSnapshot, exportedPreimages, harness.Anchor, harness.Scratch.Path, () => true);

        AssertPublishedState(harness, root, "a5");
    }

    [Test]
    public async Task Snapshot_alone_or_preimages_over_flat_publish_the_native_state(
        [Values("anchor", "a1", "a2", "a3", "a4", "a5")] string name, [Values("snapshot", "preimages")] string mode)
    {
        using Harness harness = new(name);
        ValueHash256 root;
        if (mode == "snapshot")
        {
            using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
            root = await harness.Publication.PublishSnapshot(snapshot, null, harness.Anchor, harness.Scratch.Path, () => true);
        }
        else
        {
            using BootstrapLease flat = new(harness, name, offline: true);
            using FileStream preimages = OpenArtifact(name, "preimages.bin");
            root = await harness.Publication.PublishPreimages(preimages, flat.OfflineSource!, flat.OfflineCode!, harness.Anchor, harness.Scratch.Path, () => true);
        }

        AssertPublishedState(harness, root, name);
        Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Is.Empty);
    }

    /// <remarks>Stages several times the staging batch size, so the writes span many batches written by parallel flushers.</remarks>
    [Test]
    public async Task Snapshot_staged_across_many_batches_publishes_every_account_and_slot()
    {
        using Harness harness = new("anchor");
        Address[] addresses = new Address[1000];
        List<RebuildEntry> leaves = [];
        for (int index = 0; index < addresses.Length; index++)
        {
            Address address = addresses[index] = Address.FromNumber((UInt256)(index + 1));
            ValueHash256 basicData = default;
            PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, 0, (UInt256)(index + 1), UInt256.Zero);
            leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(address, 0), basicData));
            leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(address, 1), Keccak.OfAnEmptyString.ValueHash256));
            leaves.Add(new(PbtStateKey.Storage(address, 100), new ValueHash256(((UInt256)(index + 1)).ToBigEndian())));
        }
        leaves.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        ValueHash256 expectedRoot = PbtRightmostGroupStore.CalculateRoot(leaves, PbtRightmostGroupStore.DefaultWindowSize, CancellationToken.None);
        using MemoryStream snapshot = new();
        PbtSnapshotCodec.Write(snapshot, expectedRoot, (ulong)leaves.Count, leaves);
        snapshot.Position = 0;

        ValueHash256 root = await harness.Publication.PublishSnapshot(snapshot, null, harness.Anchor, harness.Scratch.Path, () => true);

        using IPbtPersistence.IReader reader = new PbtRocksDbPersistence(harness.Target, new PbtConfig()).CreateReader();
        Assert.That(root, Is.EqualTo(expectedRoot));
        for (int index = 0; index < addresses.Length; index++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetAccount(PbtKeyDerivation.AddressKeyHash(addresses[index]))?.Nonce, Is.EqualTo((ulong)(index + 1)));
                Assert.That(reader.GetSlot(PbtStateKey.Storage(addresses[index], 100)),
                    Is.EqualTo(EvmWordSlot.FromStripped(((UInt256)(index + 1)).ToBigEndian())));
            }
        }
    }

    [Test]
    public void Bootstrap_step_follows_genesis_and_precedes_network()
    {
        RunnerStepDependenciesAttribute dependencies = (RunnerStepDependenciesAttribute)Attribute.GetCustomAttribute(
            typeof(InitializePbtMigration), typeof(RunnerStepDependenciesAttribute))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependencies.Dependencies, Does.Contain(typeof(LoadGenesisBlock)));
            Assert.That(dependencies.Dependents, Does.Contain(typeof(InitializeNetwork)));
        }
    }

    [Test]
    public async Task Fake_root_snapshot_import_switches_the_node_to_pbt_without_migrating()
    {
        using Stream input = typeof(PbtAnchorPublicationTests).Assembly.GetManifestResourceStream("Nethermind.State.Pbt.Test.Fixtures.Eip8347.genesis.json")!;
        ChainSpec chain = new GethGenesisLoader(new EthereumJsonSerializer()).Load(input);
        chain.Parameters.Eip8347TransitionTimestamp = null;
        PbtConfig config = new()
        {
            Enabled = true,
            ImportMigrationSnapshotWithFakeRoots = true,
            MigrationAnchor = 0,
            MigrationSnapshotPath = Path.Combine(Fixtures, "canonical", "anchor", "snapshot.pbt"),
            MigrationPreimagesPath = Path.Combine(Fixtures, "canonical", "anchor", "preimages.bin"),
        };
        using TempPath scratch = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(new Nethermind.Api.InitConfig { BaseDbPath = scratch.Path }), chain, useTestSpecProvider: false))
            .AddSingleton<IPbtConfig>(config)
            .AddModule(new PbtModule(config))
            .Build();
        Block genesis = Build.A.Block.Genesis.WithStateRoot(new Hash256(Metadata("anchor").GetProperty("mptRoot").GetString()!)).TestObject;
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: [genesis]);

        ImportMigrationSnapshotWithFakeRoots step = container.Resolve<ImportMigrationSnapshotWithFakeRoots>();
        await step.Execute(CancellationToken.None);
        // A restart with the same snapshot reuses the import.
        await step.Execute(CancellationToken.None);

        RunnerStepDependenciesAttribute dependencies = (RunnerStepDependenciesAttribute)Attribute.GetCustomAttribute(
            typeof(ImportMigrationSnapshotWithFakeRoots), typeof(RunnerStepDependenciesAttribute))!;
        using IPbtPersistence.IReader reader = container.Resolve<IPbtPersistence>().CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IPbtChildHeaderSource>(), Is.TypeOf<PbtBlockTreeChildHeaderSource>());
            Assert.That(container.Resolve<IWorldStateManager>().GlobalStateReader.HasStateForBlock(genesis.Header), Is.True);
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(genesis.Header)));
            Assert.That(reader.CurrentRoot, Is.EqualTo(new Hash256(Metadata("anchor").GetProperty("pbtRoot").GetString()!).ValueHash256));
            Assert.That(dependencies.Dependencies, Does.Contain(typeof(LoadGenesisBlock)));
            Assert.That(dependencies.Dependents, Is.SupersetOf(new[] { typeof(ReviewBlockTree), typeof(InitializeNetwork) }));
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
                harness.Publication.PublishSnapshot(snapshot, withPreimages ? preimages : null, harness.Anchor, harness.Scratch.Path, () => true))!;
            Assert.That(exception.Message, Does.Not.Contain("claimed root"));
        }
        AssertUnpublished(harness);
    }

    /// <summary>Corrupts one fixture's artifacts, recomputing the snapshot's claimed PBT root over the corrupted leaves.</summary>
    private static (MemoryStream Snapshot, MemoryStream Preimages) Corrupt(string name, string corruption)
    {
        using FileStream original = OpenArtifact(name, "snapshot.pbt");
        (_, ulong count) = PbtSnapshotCodec.ReadHeader(original);
        List<RebuildEntry> leaves = [.. PbtSnapshotCodec.ReadLeaves(original, count)];
        using FileStream originalPreimages = OpenArtifact(name, "preimages.bin");
        List<PbtAccountPreimages> accounts = ReadPreimages(originalPreimages);
        Address writer = new("0x1000000000000000000000000000000000000001");
        Address authority = new("0x2b5ad5c4795c026514f8317c7a215e218dccd6cf");
        Address history = new("0x0000f90827f1c53a10cb7a02335b175320002935");
        ValueHash256 writerCodeHash = ValueKeccak.Compute(Bytes.FromHexString("60003560005500"));
        PbtStorageTreeKey basic = (PbtStorageTreeKey)PbtStateKey.Account(writer, 0);
        PbtStorageTreeKey hash = (PbtStorageTreeKey)PbtStateKey.Account(writer, 1);
        PbtStorageTreeKey chunk = (PbtStorageTreeKey)PbtStateKey.Code(writerCodeHash, 0);
        PbtStorageTreeKey delegation = (PbtStorageTreeKey)PbtStateKey.Account(authority, 2);
        PbtStorageTreeKey storageKey = PbtStateKey.Storage(history, UInt256.Zero);
        switch (corruption)
        {
            case "code": Mutate(chunk, 1); break;
            case "pushdata": Mutate(chunk, 0); break;
            case "padding": Mutate(chunk, 31); break;
            case "code-size": Mutate(basic, 7); break;
            case "version": Mutate(basic, 0); break;
            case "reserved": Mutate(basic, 3); break;
            case "nonce": Mutate(basic, 15); break;
            case "balance": Mutate(basic, 31); break;
            case "storage": Mutate(storageKey, 31); break;
            case "missing-storage": Remove(storageKey); break;
            case "missing-basic": Remove(basic); break;
            case "missing-code-hash": Remove(hash); break;
            case "missing-code": Remove(chunk); break;
            case "delegation-prefix": Mutate(delegation, 0); break;
            case "delegation-padding": Mutate(delegation, 31); break;
            case "delegation-size": Mutate((PbtStorageTreeKey)PbtStateKey.Account(authority, 0), 7); break;
            case "delegation-code-hash": leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(authority, 1), Keccak.OfAnEmptyString.ValueHash256)); break;
            case "delegation-with-code-leaves":
                byte[] delegationCode = leaves.Find(entry => entry.Key.Equals(delegation)).Leaf.Bytes[..23].ToArray();
                leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Code(ValueKeccak.Compute(delegationCode), 0), new ValueHash256(PbtKeyDerivation.ChunkifyCode(delegationCode))));
                break;
            case "reserved-sub-index": leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(Address.Zero, 3), Keccak.OfAnEmptyString.ValueHash256)); break;
            // A chunk past the account's code size: reachable by no code read, so nothing accounts for it.
            case "extra-code-chunk": leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Code(writerCodeHash, 1), Keccak.OfAnEmptyString.ValueHash256)); break;
            case "codeless-without-code-hash": Remove((PbtStorageTreeKey)PbtStateKey.Account(Eoa(), 1)); break;
            case "surplus-account-leaves":
                Address surplus = new("0x00000000000000000000000000000000deadbeef");
                ValueHash256 basicData = default;
                PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, 0, 1, UInt256.Zero);
                leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(surplus, 0), basicData));
                leaves.Add(new((PbtStorageTreeKey)PbtStateKey.Account(surplus, 1), Keccak.OfAnEmptyString.ValueHash256));
                break;
            case "orphan-storage-leaf":
                leaves.Add(new(PbtStateKey.Storage(new Address("0x00000000000000000000000000000000cafebabe"), 100),
                    new ValueHash256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001"))));
                break;
            // Both artifacts drop one account consistently: the image is whole, but not the anchor's state.
            case "anchored-elsewhere":
                Address dropped = Eoa();
                ValueHash256 droppedStem = PbtKeyDerivation.AddressKeyHash(dropped);
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
        ValueHash256 attackerRoot = PbtRightmostGroupStore.CalculateRoot(leaves, PbtRightmostGroupStore.DefaultWindowSize, CancellationToken.None);
        MemoryStream snapshot = new();
        PbtSnapshotCodec.Write(snapshot, attackerRoot, (ulong)leaves.Count, leaves);
        snapshot.Position = 0;
        MemoryStream preimages = new();
        PbtPreimageCodec.Write(preimages, accounts);
        preimages.Position = 0;
        return (snapshot, preimages);

        void Mutate(PbtStorageTreeKey key, int offset)
        {
            int index = leaves.FindIndex(entry => entry.Key.Equals(key));
            Assert.That(index, Is.GreaterThanOrEqualTo(0), corruption);
            byte[] bytes = leaves[index].Leaf.Bytes.ToArray();
            bytes[offset] ^= 1;
            leaves[index] = new(key, new ValueHash256(bytes));
        }

        void Remove(PbtStorageTreeKey key) => Assert.That(leaves.RemoveAll(entry => entry.Key.Equals(key)), Is.EqualTo(1));

        // A codeless account without storage, whose leaves are only its basic data and empty code hash.
        Address Eoa() => accounts.Find(account => account.SlotCount == 0 && leaves.Exists(entry =>
            entry.Key.Equals((PbtStorageTreeKey)PbtStateKey.Account(account.Address, 1)) && entry.Leaf == Keccak.OfAnEmptyString.ValueHash256)).Address;

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

    private static List<PbtAccountPreimages> ReadPreimages(Stream source)
    {
        List<PbtAccountPreimages> accounts = [];
        PbtPreimageReader reader = new(source);
        while (reader.ReadAccount(out Address? address, out uint count))
        {
            List<ValueHash256> slots = [];
            for (uint index = 0; index < count; index++) slots.Add(reader.ReadSlot());
            accounts.Add(new(address!, count, slots));
        }
        return accounts;
    }

    private static void AssertUnpublished(Harness harness)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Pbt.Manager.HasStateForBlock(new StateId(harness.Anchor.Header)), Is.False);
            Assert.That(harness.Target.GetColumnDb(PbtColumns.Metadata).Get("validState"u8), Is.Null);
            Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Is.Empty);
        }
    }

    /// <remarks>Opening the persistence stamps the schema epoch and key layout; nothing else may be there.</remarks>
    private static void AssertNoNativeState(Harness harness)
    {
        foreach (PbtColumns column in harness.Target.ColumnKeys)
        {
            IEnumerable<byte[]> keys = harness.Target.GetColumnDb(column).GetAllKeys();
            if (column == PbtColumns.Metadata) Assert.That(keys, Is.EquivalentTo(new[] { "schemaEpoch"u8.ToArray(), "nodeGroupKeyLayout"u8.ToArray(), "prefixlessBranchOmission"u8.ToArray() }));
            else Assert.That(keys, Is.Empty, column.ToString());
        }
    }

    private static void AssertPublishedState(Harness harness, ValueHash256 root, string name)
    {
        PbtConfig config = new();
        PbtRocksDbPersistence reopened = new(harness.Target, config);
        using IPbtPersistence.IReader reader = reopened.CreateReader();
        Hash256 expectedRoot = new(Metadata(name).GetProperty("pbtRoot").GetString()!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expectedRoot.ValueHash256));
            Assert.That(reader.CurrentRoot, Is.EqualTo(expectedRoot.ValueHash256));
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(harness.Anchor.Header)));
            Assert.That(harness.Pbt.Manager.HasStateForBlock(new StateId(harness.Anchor.Header)), Is.True, "the live manager sees the imported anchor");
        }
        using JsonDocument state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "states", name + ".alloc.json")));
        foreach (JsonProperty property in state.RootElement.EnumerateObject())
        {
            Address address = new(property.Name);
            JsonElement expected = property.Value;
            Account? account = reader.GetAccount(PbtKeyDerivation.AddressKeyHash(address));
            Assert.That(account, Is.Not.Null, property.Name);
            byte[] code = expected.TryGetProperty("code", out JsonElement codeJson) ? Bytes.FromHexString(codeJson.GetString()!) : [];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(account!.Nonce, Is.EqualTo(expected.TryGetProperty("nonce", out JsonElement nonce) ? (ulong)Number(nonce.GetString()!) : 0UL), property.Name);
                Assert.That(account.Balance, Is.EqualTo(Number(expected.GetProperty("balance").GetString()!)), property.Name);
                Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(code)), property.Name);
                if (code.Length != 0) Assert.That(reader.GetCode(account.CodeHash.ValueHash256)?.Code.ToArray(), Is.EqualTo(code), property.Name);
            }
            if (!expected.TryGetProperty("storage", out JsonElement storage)) continue;
            foreach (JsonProperty slot in storage.EnumerateObject())
                Assert.That(reader.GetSlot(PbtStateKey.Storage(address, Number(slot.Name))),
                    Is.EqualTo(EvmWordSlot.FromStripped(Number(slot.Value.GetString()!).ToBigEndian())), slot.Name);
        }

        using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
        (_, ulong count) = PbtSnapshotCodec.ReadHeader(snapshot);
        using PbtNodeGroupStore oracle = new();
        List<(byte[] Key, byte[]? Value)> leaves = [];
        foreach (RebuildEntry leaf in PbtSnapshotCodec.ReadLeaves(snapshot, count)) leaves.Add((leaf.Key.Bytes.ToArray(), leaf.Leaf.ToByteArray()));
        Assert.That(oracle.Fold(default, leaves, PbtTreeHarness.DefaultFanOut, null), Is.EqualTo(expectedRoot.ValueHash256));
        using IPbtIterator<PbtStorageNodePath> groupKeys = reader.EnumerateNodeGroupKeys();
        Assert.That(CanonicalGroups(groupKeys.Drain(), reader.GetNodeGroup),
            Is.EqualTo(CanonicalGroups(oracle.EnumerateNodeGroupKeys(), oracle.GetPhysicalNodeGroup)));
    }

    private static string[] CanonicalGroups(IEnumerable<PbtStorageNodePath> keys, Func<PbtStorageNodePath, RefCountingMemory?> getGroup)
    {
        List<string> groups = [];
        foreach (PbtStorageNodePath key in keys)
        {
            using RefCountingMemory? payload = getGroup(key);
            groups.Add($"{Convert.ToHexString(key.ToEncodedArray())}:{Convert.ToHexString(payload!.GetSpan())}");
        }
        groups.Sort(StringComparer.Ordinal);
        return [.. groups];
    }

    private static FileStream OpenArtifact(string name, string file) => File.OpenRead(Path.Combine(Fixtures, "canonical", name, file));
    private static UInt256 Number(string hex) => new(Bytes.FromHexString(hex), isBigEndian: true);
    private static JsonElement Metadata(string name)
    {
        using JsonDocument blocks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "blocks.json")));
        foreach (JsonElement block in blocks.RootElement.EnumerateArray())
            if (block.GetProperty("name").GetString() == name) return block.Clone();
        throw new ArgumentException("Unknown fixture", nameof(name));
    }

    private sealed class Harness : IDisposable
    {
        public readonly TempPath Scratch = TempPath.GetTempDirectory();
        public readonly SyncColumnsDb Target = new();
        public PbtImageAnchor Anchor;
        public Func<bool> IsAnchorCurrent = () => true;
        public PbtTestContext Pbt { get; private set; } = null!;
        private PbtAnchorPublication? _publication;
        public PbtAnchorPublication Publication => _publication ??=
            new PbtAnchorPublication(new PbtRocksDbPersistence(Target, _config), Target, Pbt.Persistence, Pbt.Manager, Pbt.Coordinator, _config, LimboLogs.Instance);
        private readonly string _name;
        private readonly PbtConfig _config;

        public Harness(string name) : this(name, new PbtConfig()) { }

        public Harness(string name, PbtConfig config)
        {
            _name = name;
            _config = config;
            Directory.CreateDirectory(Scratch.Path);
            JsonElement metadata = Metadata(name);
            BlockHeader header = Build.A.BlockHeader.WithNumber(metadata.GetProperty("number").GetUInt64())
                .WithTimestamp(0).WithStateRoot(new Hash256(metadata.GetProperty("mptRoot").GetString()!)).TestObject;
            Anchor = new("1", new Hash256(Metadata("anchor").GetProperty("blockHash").GetString()!), header, 48, 24576);
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
            _publication = null;
        }

        public async Task<ValueHash256> Publish(CancellationToken cancellationToken = default)
        {
            using FileStream snapshot = OpenArtifact(_name, "snapshot.pbt");
            using FileStream preimages = OpenArtifact(_name, "preimages.bin");
            return await Publication.PublishSnapshot(snapshot, preimages, Anchor, Scratch.Path, IsAnchorCurrent, cancellationToken);
        }

        public void Dispose()
        {
            Pbt.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Target.Dispose();
            Scratch.Dispose();
        }
    }

    private sealed class BootstrapLease : PbtBootstrapLease
    {
        private readonly Harness _harness;
        private readonly SnapshotableMemColumnsDb<FlatDbColumns> _sourceDatabase = new();
        private readonly MemDb _code = new();
        private readonly IPersistence.IPersistenceReader? _sourceReader;
        private readonly FileStream? _snapshot, _preimages;
        public bool Disposed { get; private set; }

        public BootstrapLease(Harness harness, string name, bool offline)
        {
            _harness = harness;
            if (offline)
            {
                PreimageRocksdbPersistence source = new(_sourceDatabase, LimboLogs.Instance, FlatLayout.PreimageFlat);
                using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
                using FileStream preimages = OpenArtifact(name, "preimages.bin");
                using (IPersistence.IWriteBatch batch = source.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(Anchor.Header), WriteFlags.None))
                    Eip8347FixtureState.Replay(snapshot, preimages, (address, account, code) =>
                    {
                        batch.SetAccount(address, account);
                        if (code.Length != 0) _code[account.CodeHash.Bytes] = code;
                    }, (address, slot, value) => batch.SetStorage(address, slot, value));
                _sourceReader = source.CreateReader();
            }
            else
            {
                _snapshot = OpenArtifact(name, "snapshot.pbt");
                _preimages = OpenArtifact(name, "preimages.bin");
            }
        }

        public override PbtImageAnchor Anchor => _harness.Anchor;
        public override string ScratchDirectory => _harness.Scratch.Path;
        public override Stream? Snapshot => _snapshot;
        public override Stream? Preimages => _preimages;
        public override IPersistence.IPersistenceReader? OfflineSource => _sourceReader;
        public override IReadOnlyKeyValueStore? OfflineCode => _sourceReader is null ? null : _code;
        public override bool IsAnchorCurrent() => _harness.IsAnchorCurrent();
        public override void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            _snapshot?.Dispose(); _preimages?.Dispose(); _sourceReader?.Dispose();
            _code.Dispose(); _sourceDatabase.Dispose();
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

    private sealed class SyncDb(IDb database, Action sync) : IDb
    {
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
    }
}
