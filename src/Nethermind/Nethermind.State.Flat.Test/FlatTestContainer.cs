// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Builds the persisted-tier flatdb component graph the way production does — by loading
/// <see cref="FlatWorldStateModule"/> into an Autofac container — then overlays the handful of
/// test-only overrides every fixture needs: a temp <c>BaseDbPath</c>, in-memory catalog/metadata
/// <see cref="MemDb"/>s, <see cref="LimboLogs"/>, a cancellable <see cref="IProcessExitSource"/>, and a
/// blob arena sized independently of the trie-RLP arena. Resolving any persisted-tier component returns
/// the same singletons the production module wires, so tests run against a prod-representative graph.
/// </summary>
/// <remarks>
/// The container builds lazily on first resolve; building runs <see cref="IPersistedSnapshotLoader.Load"/>,
/// and disposal tears down the loader before the temp dir is removed. Reopen/restart tests build a second
/// <see cref="FlatTestContainer"/> over the same <see cref="BaseDbPath"/> and the same
/// <see cref="CatalogDb"/> instance to verify data survives a restart.
/// </remarks>
internal sealed class FlatTestContainer : IDisposable
{
    private readonly ContainerBuilder _builder;
    private readonly CancellationTokenSource _cts = new();
    private readonly TempPath? _ownedTempDir;
    private IContainer? _container;

    public FlatDbConfig Config { get; }

    /// <summary>Data directory the persisted tier lives under; pass it to a second container to reopen.</summary>
    public string BaseDbPath { get; }

    /// <summary>The in-memory catalog; pass it to a second container to simulate a restart.</summary>
    public IDb CatalogDb { get; }

    public FlatTestContainer(
        FlatDbConfig? config = null,
        long arenaFileSizeBytes = 1024L * 1024 * 1024,
        long blobFileSizeBytes = 1024L * 1024,
        long arenaPageCacheBytes = 0,
        string? baseDbPath = null,
        IDb? catalogDb = null,
        Action<ContainerBuilder>? configure = null)
    {
        Config = config ?? new FlatDbConfig();
        Config.ArenaFileSizeBytes = arenaFileSizeBytes;
        Config.PersistedSnapshotArenaPageCacheBytes = arenaPageCacheBytes;

        if (baseDbPath is null)
        {
            _ownedTempDir = TempPath.GetTempDirectory();
            BaseDbPath = _ownedTempDir.Path;
        }
        else
        {
            BaseDbPath = baseDbPath;
        }

        CatalogDb = catalogDb ?? new MemDb();

        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(_cts.Token);

        _builder = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(Config))
            .AddSingleton<IFlatDbConfig>(Config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IInitConfig>(new InitConfig { BaseDbPath = BaseDbPath })
            .AddSingleton<IProcessExitSource>(processExitSource)
            // The production module wires the catalog and metadata to columned RocksDB via IDbFactory,
            // which the test project does not provide; an in-memory db is behavior-equivalent here.
            .AddKeyedSingleton<IDb>(DbNames.PersistedSnapshotCatalog, CatalogDb)
            .AddKeyedSingleton<IDb>(DbNames.Metadata, new MemDb())
            // The module sizes the blob arena off ArenaFileSizeBytes (shared with the trie-RLP arena);
            // tests size the two independently, so override the blob arena's file size.
            .AddSingleton<BlobArenaManager, IInitConfig>(initConfig =>
                new BlobArenaManager(Path.Combine(initConfig.BaseDbPath, "persisted_snapshot", "blob"), blobFileSizeBytes))
            // Config defaults to EnableLongFinality=false, which makes the module swap in the Null
            // catalog/loader. These fixtures exercise the real persisted tier, so force the real catalog
            // back (last-registration wins); the real loader is reached via concrete resolves below.
            .AddSingleton<ISnapshotCatalog>(ctx => ctx.Resolve<SnapshotCatalog>());

        configure?.Invoke(_builder);
    }

    private IContainer Container => _container ??= BuildAndLoad();

    private IContainer BuildAndLoad()
    {
        IContainer container = _builder.Build();
        container.Resolve<PersistedSnapshotLoader>().Load();
        return container;
    }

    public T Resolve<T>() where T : notnull => Container.Resolve<T>();

    public SnapshotRepository Repository => Resolve<SnapshotRepository>();
    public IPersistedSnapshotLoader Loader => Resolve<PersistedSnapshotLoader>();
    public ResourcePool ResourcePool => Resolve<ResourcePool>();
    public ArenaManager Arena => Resolve<ArenaManager>();
    public BlobArenaManager Blobs => Resolve<BlobArenaManager>();
    public PersistedSnapshotCompactor Compactor => Resolve<PersistedSnapshotCompactor>();

    /// <summary>Converts <paramref name="snapshot"/> to a persisted base via the production loader and
    /// returns it pre-leased from the repository so callers hold a disposable handle for assertions.</summary>
    public PersistedSnapshot ConvertToPersistedBase(Snapshot snapshot)
    {
        Loader.ConvertAndRegister(snapshot);
        using PersistedSnapshotList bases = Repository.LeaseBaseSnapshotsInRange(snapshot.From, snapshot.To);
        PersistedSnapshot persisted = bases[0];
        _ = persisted.TryAcquire();
        return persisted;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _container?.Dispose();
        _cts.Dispose();
        _ownedTempDir?.Dispose();
    }
}
