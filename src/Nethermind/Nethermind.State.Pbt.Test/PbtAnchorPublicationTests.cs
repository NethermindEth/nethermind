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
        [Values("anchor", "a1", "a2", "a3", "a4", "a5")] string name, [Values(4096, 1)] int maxBufferedRuns)
    {
        using Harness harness = new(name) { MaxBufferedRuns = maxBufferedRuns };
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
    public void Image_that_does_not_rebuild_the_anchor_root_does_not_stage_or_publish()
    {
        using Harness harness = new("anchor");
        harness.Anchor = harness.WithStateRoot(Hash256.Zero);

        Assert.ThrowsAsync<InvalidDataException>(() => harness.Publish());

        AssertUnpublished(harness);
        AssertNoNativeState(harness);
    }

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
    public async Task Interrupted_native_anchor_recovers_only_matching_prepared_source([Values] bool changeSource)
    {
        using Harness harness = new("a4");
        using CancellationTokenSource cancellation = new();
        harness.Target.OnSync = () =>
        {
            if (harness.Target.GetColumnDb(PbtColumns.Metadata).Get("validState"u8) is not null) cancellation.Cancel();
        };
        Assert.ThrowsAsync<OperationCanceledException>(() => harness.Publish(cancellation.Token));
        harness.Target.OnSync = () => { };
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

        ValueHash256 root = await harness.Publication.Publish(exportedSnapshot, exportedPreimages, harness.Anchor, harness.Scratch.Path, () => true);

        AssertPublishedState(harness, root, "a5");
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

    private static void AssertUnpublished(Harness harness)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Pbt.Manager.HasStateForBlock(new StateId(harness.Anchor.Header)), Is.False);
            Assert.That(new PbtRocksDbPersistence(harness.Target, new PbtConfig()).IsValid, Is.False);
            Assert.That(Directory.GetFileSystemEntries(harness.Scratch.Path), Is.Empty);
        }
    }

    /// <remarks>Opening the persistence stamps the schema epoch and key layout; nothing else may be there.</remarks>
    private static void AssertNoNativeState(Harness harness)
    {
        foreach (PbtColumns column in harness.Target.ColumnKeys)
        {
            IEnumerable<byte[]> keys = harness.Target.GetColumnDb(column).GetAllKeys();
            if (column == PbtColumns.Metadata) Assert.That(keys, Is.EquivalentTo(new[] { "schemaEpoch"u8.ToArray(), "nodeGroupKeyLayout"u8.ToArray() }));
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
        Assert.That(oracle.Fold(default, leaves, config.PrefixlessBranchOmission, FoldFanOut.Default, null), Is.EqualTo(expectedRoot.ValueHash256));
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
        public int MaxBufferedRuns { get; init; } = 4096;
        public PbtTestContext Pbt { get; private set; } = null!;
        private PbtAnchorPublication? _publication;
        // Created on first use so the initializer's MaxBufferedRuns applies.
        public PbtAnchorPublication Publication => _publication ??=
            new PbtAnchorPublication(new PbtRocksDbPersistence(Target, new PbtConfig()), Target, Pbt.Persistence, Pbt.Manager, Pbt.Coordinator, new PbtConfig(), LimboLogs.Instance) { MaxBufferedRuns = MaxBufferedRuns };
        private readonly string _name;

        public Harness(string name)
        {
            _name = name;
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
            Pbt = new PbtTestContext(Target);
            _publication = null;
        }

        public async Task<ValueHash256> Publish(CancellationToken cancellationToken = default)
        {
            using FileStream snapshot = OpenArtifact(_name, "snapshot.pbt");
            using FileStream preimages = OpenArtifact(_name, "preimages.bin");
            return await Publication.Publish(snapshot, preimages, Anchor, Scratch.Path, IsAnchorCurrent, cancellationToken);
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
        private readonly SnapshotableMemColumnsDb<FlatDbColumns> _mptDatabase = new(), _sourceDatabase = new();
        private readonly MemDb _code = new();
        private readonly IPersistence.IPersistenceReader _mptReader;
        private readonly IPersistence.IPersistenceReader? _sourceReader;
        private readonly FileStream? _snapshot, _preimages;
        public bool Disposed { get; private set; }

        public BootstrapLease(Harness harness, string name, bool offline, string? mismatch = null)
        {
            _harness = harness;
            IPersistence mpt = mismatch == "preimage"
                ? new PreimageRocksdbPersistence(_mptDatabase, LimboLogs.Instance, FlatLayout.PreimageFlat)
                : new RocksDbPersistence(_mptDatabase, LimboLogs.Instance);
            FlatStateId state = new(mismatch == "number" ? Anchor.Header.Number + 1 : Anchor.Header.Number,
                mismatch == "root" ? default : Anchor.Header.StateRoot!.ValueHash256);
            using (IPersistence.IWriteBatch batch = mpt.CreateWriteBatch(FlatStateId.PreGenesis, state, WriteFlags.None)) { }
            _mptReader = mpt.CreateReader();
            if (offline)
            {
                PreimageRocksdbPersistence source = new(_sourceDatabase, LimboLogs.Instance, FlatLayout.PreimageFlat);
                using FileStream snapshot = OpenArtifact(name, "snapshot.pbt");
                using FileStream preimages = OpenArtifact(name, "preimages.bin");
                using PbtVerifiedImage image = PbtImageVerifier.Verify(snapshot, preimages, Anchor, ScratchDirectory, LimboLogs.Instance);
                using (IPersistence.IWriteBatch batch = source.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(Anchor.Header), WriteFlags.None))
                    image.Replay((address, account, code) =>
                    {
                        batch.SetAccount(address, account);
                        if (code.Length != 0) _code[account.CodeHash.Bytes] = code;
                    }, (address, slot, value) => batch.SetStorage(address, slot, new UInt256(value.Bytes, true)));
                _sourceReader = source.CreateReader();
            }
            else
            {
                _snapshot = OpenArtifact(name, "snapshot.pbt");
                _preimages = OpenArtifact(name, "preimages.bin");
            }
        }

        public override PbtImageAnchor Anchor => _harness.Anchor;
        public override IPersistence.IPersistenceReader MptAnchor => _mptReader;
        public override IColumnsDb<PbtColumns> Target => _harness.Target;
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
            _snapshot?.Dispose(); _preimages?.Dispose(); _mptReader.Dispose(); _sourceReader?.Dispose();
            _code.Dispose(); _sourceDatabase.Dispose(); _mptDatabase.Dispose();
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
