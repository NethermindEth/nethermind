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
    private readonly IContainer _container;

    public FlatManagerContainer(FlatDbConfig config, string basePath, IStateHeaderProvider finality, IProcessExitSource? processExitSource = null)
    {
        Db = new FlatTestDbs.TrackingColumnsDb(Path.Combine(basePath, DbNames.Flat));
        _container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<IColumnsDb<FlatDbColumns>>(Db, takeOwnership: true)
            .AddSingleton<IDbFactory, MemDbFactory>()
            .AddKeyedSingleton<IDb>(DbNames.Metadata, new MemDb())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<IBlocksConfig>(new BlocksConfig())
            .AddSingleton<IInitConfig>(new InitConfig { BaseDbPath = basePath })
            .AddSingleton<IProcessExitSource>(processExitSource ?? new ProcessExitSource(CancellationToken.None))
            .AddSingleton<IStatePersistenceBarrier>(NullStatePersistenceBarrier.Instance)
            .AddSingleton<IStateHeaderProvider>(finality)
            .Build();
        Manager = _container.Resolve<IFlatDbManager>();
        PersistenceManager = _container.Resolve<IPersistenceManager>();
        ResourcePool = _container.Resolve<IResourcePool>();
    }

    public FlatTestDbs.TrackingColumnsDb Db { get; }

    public IFlatDbManager Manager { get; }

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

    /// <summary>The config of a test: compaction every 4 blocks at a fixed offset, inline, and a shallow reorg window.</summary>
    public static FlatDbConfig Config(FlatDbBackend backend, FlatLayout layout, bool carryForward, ulong maxInMemorySnapshotBytes = 0) => new()
    {
        Enabled = true,
        Backend = backend,
        Layout = layout,
        EnableCarryForwardCache = carryForward,
        EnableLongFinality = false,
        CompactSize = 4,
        CompactionOffset = 0,
        InlineCompaction = true,
        MinReorgDepth = 2,
        MaxReorgDepth = 12,
        MaxInMemorySnapshotBytes = maxInMemorySnapshotBytes,
        TrieWarmerWorkerCount = 0,
    };

    public ValueTask DisposeAsync() => _container.DisposeAsync();
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
