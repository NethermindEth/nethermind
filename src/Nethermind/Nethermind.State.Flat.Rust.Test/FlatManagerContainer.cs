// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The flat DB of one backend wired as production wires it, <see cref="FlatWorldStateModule"/>, over a RocksDB in
/// <paramref name="basePath"/>, with the finality of <paramref name="finality"/>.
/// </summary>
internal sealed class FlatManagerContainer : IAsyncDisposable
{
    /// <summary>The account <see cref="AddBlock"/> sets.</summary>
    public static readonly Address BlockAddress = new("0xaaaaaaaa00000000000000000000000000000001");

    private readonly IContainer _container;

    public FlatManagerContainer(FlatDbConfig config, string basePath, IStateHeaderProvider finality)
        : this(config, basePath, finality, new Overrides())
    {
    }

    public FlatManagerContainer(FlatDbConfig config, string basePath, IStateHeaderProvider finality, Overrides overrides)
    {
        Db = overrides.Db ?? new FlatTestDbs.TrackingColumnsDb(Path.Combine(basePath, DbNames.Flat));
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<IColumnsDb<FlatDbColumns>>(Db, takeOwnership: true)
            .AddSingleton<IDbFactory, MemDbFactory>()
            .AddKeyedSingleton<IDb>(DbNames.Metadata, new MemDb())
            .AddSingleton<ILogManager>(overrides.LogManager ?? LimboLogs.Instance)
            .AddSingleton<IMetricsConfig>(overrides.MetricsConfig ?? new MetricsConfig())
            .AddSingleton<IBlocksConfig>(new BlocksConfig())
            .AddSingleton<IInitConfig>(new InitConfig { BaseDbPath = basePath })
            .AddSingleton<IProcessExitSource>(overrides.ProcessExitSource ?? new ProcessExitSource(CancellationToken.None))
            .AddSingleton<IStatePersistenceBarrier>(overrides.PersistenceBarrier ?? NullStatePersistenceBarrier.Instance)
            .AddSingleton<IStateHeaderProvider>(finality);
        if (overrides.TrieNodeCache is not null) builder.AddSingleton<ITrieNodeCache>(overrides.TrieNodeCache);
        if (overrides.RocksDbCatalog) builder.AddKeyedSingleton<IDb>(DbNames.PersistedSnapshotCatalog, _ => FlatTestDbs.OpenRocksDbCatalog(basePath));
        _container = builder.Build();
        PersistenceManager = _container.Resolve<IPersistenceManager>();
        ResourcePool = _container.Resolve<IResourcePool>();
        Arm = config.Backend == FlatDbBackend.Rust
            ? new FlatArm.Rust(_container.Resolve<Fork.IFlatDbManager>(), ResourcePool)
            : new FlatArm.CSharp(_container.Resolve<IFlatDbManager>(), ResourcePool);
    }

    public FlatTestDbs.TrackingColumnsDb Db { get; }

    /// <summary>The snapshot manager of the backend: the forked one on <see cref="FlatDbBackend.Rust"/>, else the original.</summary>
    public FlatArm Arm { get; }

    public IPersistenceManager PersistenceManager { get; }

    public IResourcePool ResourcePool { get; }

    public T Resolve<T>() where T : notnull => _container.Resolve<T>();

    /// <summary>Runs <see cref="IPersistenceManager.AddToPersistence"/> for <paramref name="head"/> until the persisted state stops moving.</summary>
    public void PersistUpTo(in StateId head) => PersistUpTo(PersistenceManager, head);

    /// <inheritdoc cref="PersistUpTo(in StateId)"/>
    /// <remarks>One call persists at most four snapshots; the rest would otherwise drain on the background workers.</remarks>
    public static void PersistUpTo(IPersistenceManager persistenceManager, in StateId head)
    {
        StateId persisted;
        do
        {
            persisted = persistenceManager.GetCurrentPersistedStateId();
            persistenceManager.AddToPersistence(head).GetAwaiter().GetResult();
        } while (persistenceManager.GetCurrentPersistedStateId() != persisted);
    }

    /// <summary>Adds block <paramref name="number"/> on its parent, which sets the account at <see cref="BlockAddress"/> to its number.</summary>
    public void AddBlock(ulong number) =>
        new RustFlatDbManagerDifferentialTests.BlockPlan([(BlockAddress, new Account(number, number))], [], [], [], [])
            .AddTo(this, number == 0 ? StateId.PreGenesis : Block(number - 1), Block(number));

    /// <summary>The state of block <paramref name="number"/> of the chains the tests build.</summary>
    public static StateId Block(ulong number) => new(number, Keccak.Compute($"block {number}"));

    /// <inheritdoc cref="Config(FlatDbBackend, FlatLayout, bool, ulong, bool)"/>
    /// <remarks>On the flat layout, without the carry-forward cache, a snapshot byte budget or long finality.</remarks>
    public static FlatDbConfig Config(FlatDbBackend backend) =>
        Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false);

    /// <summary>The config of a test: compaction every 4 blocks at a fixed offset, inline, and a shallow reorg window.</summary>
    /// <param name="longFinality">Whether snapshots past 8 in memory are converted into a persisted-snapshot tier of
    /// small files, compacted up to 16 blocks wide, with a backstop 48 blocks deep.</param>
    public static FlatDbConfig Config(FlatDbBackend backend, FlatLayout layout, bool carryForward, ulong maxInMemorySnapshotBytes, bool longFinality)
    {
        FlatDbConfig config = new()
        {
            Enabled = true,
            Backend = backend,
            Layout = layout,
            EnableCarryForwardCache = carryForward,
            EnableLongFinality = longFinality,
            CompactSize = 4,
            CompactionOffset = 0,
            InlineCompaction = true,
            MinReorgDepth = 2,
            MaxReorgDepth = 12,
            MaxInMemorySnapshotBytes = maxInMemorySnapshotBytes,
            TrieWarmerWorkerCount = 0,
        };
        if (!longFinality) return config;

        config.MaxInMemoryBaseSnapshotCount = 8;
        config.LongFinalityMaxReorgDepth = 48;
        config.PersistedSnapshotMaxCompactSize = 16;
        config.ArenaFileSizeBytes = 64 * 1024;
        config.PersistedSnapshotDedicatedArenaThresholdBytes = 16 * 1024;
        config.PersistedSnapshotArenaPageCacheBytes = 0;
        config.PersistedSnapshotBloomBitsPerKey = 14;
        return config;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>The services a test replaces; each one left null keeps the container's own.</summary>
    public sealed record Overrides
    {
        public IProcessExitSource? ProcessExitSource { get; init; }
        public ILogManager? LogManager { get; init; }
        public IMetricsConfig? MetricsConfig { get; init; }

        /// <summary>The flat DB, in place of a RocksDB in the base path.</summary>
        public FlatTestDbs.TrackingColumnsDb? Db { get; init; }

        public IStatePersistenceBarrier? PersistenceBarrier { get; init; }
        public ITrieNodeCache? TrieNodeCache { get; init; }

        /// <summary>Whether the persisted-snapshot catalog is a RocksDB in the base path, which a reopen reads again, rather than in memory.</summary>
        public bool RocksDbCatalog { get; init; }
    }
}

/// <summary>A finality the test moves: a finalized block and the canonical roots at or below it.</summary>
internal sealed class TestFinality : IStateHeaderProvider
{
    private readonly ConcurrentDictionary<ulong, Hash256> _canonicalRoots = new();
    private long _finalizedBlockNumber;

    public ulong FinalizedBlockNumber => (ulong)Interlocked.Read(ref _finalizedBlockNumber);

    /// <summary>Finalizes <paramref name="blockNumber"/> with the canonical states of <paramref name="canonical"/>, indexed by block.</summary>
    public void Finalize(ulong blockNumber, Func<ulong, StateId> canonical)
    {
        for (ulong block = FinalizedBlockNumber; block <= blockNumber; block++)
        {
            _canonicalRoots[block] = canonical(block).StateRoot.ToCommitment();
        }

        Interlocked.Exchange(ref _finalizedBlockNumber, (long)blockNumber);
    }

    public BlockHeader? FindParentHeader(BlockHeader target) => null;

    public BlockHeader? GetFinalizedHeader(ulong blockNumber) =>
        blockNumber <= FinalizedBlockNumber && _canonicalRoots.TryGetValue(blockNumber, out Hash256? root)
            ? Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(root).TestObject
            : null;
}

/// <summary>A deferred block-data flush the test holds or fails, as every persist runs it first.</summary>
internal sealed class TestPersistenceBarrier : IStatePersistenceBarrier
{
    private readonly ManualResetEventSlim _open = new(initialState: true);
    private readonly SemaphoreSlim _held = new(0);

    /// <summary>Thrown by every flush while set.</summary>
    public Exception? Failure { get; set; }

    public bool IsEnabled => true;

    public void RegisterDrain(Action drain) { }

    public void RegisterFlush(Action flush) { }

    /// <summary>Makes the flushes wait until <see cref="Release"/>.</summary>
    public void Hold() => _open.Reset();

    public void Release() => _open.Set();

    /// <summary>Waits until a flush waits in the held barrier.</summary>
    public bool WaitHeld(TimeSpan timeout) => _held.Wait(timeout);

    public void FlushDeferred()
    {
        if (Failure is { } failure) throw failure;
        if (_open.IsSet) return;

        _held.Release();
        _open.Wait();
    }
}
