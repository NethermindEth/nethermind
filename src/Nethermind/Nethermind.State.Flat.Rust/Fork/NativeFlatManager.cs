// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// The native snapshot manager — <c>FlatDbManager</c> and <c>PersistenceManager</c> in the Rust library — over the store
/// of <see cref="RustPersistence"/>, shared by <see cref="RustFlatDbManager"/> and <see cref="RustPersistenceManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// It calls back into the services <c>PersistenceManager</c> injects through <see cref="RustHostCallbacks"/>, and
/// signals process exit to the native manager as <c>FlatDbManager</c> watches <see cref="IProcessExitSource.Token"/>.
/// A background loop applies the persisted-node events to <see cref="Replay"/>, publishes the metrics, logs what the
/// store and the native workers logged and reports a fatal RocksDB error a native persist hit to the database it hit, as
/// <see cref="RustFlatStore.ThrowIfFailed"/> reports one an export hit; the metrics are also published on every metrics
/// tick with <see cref="PublishMetrics"/>. Disposing it drains and stops the native workers, then frees the manager once
/// the last bundle or scope gathered from it is released; the store and its database stay open until then, as the
/// manager holds a reference on the store.
/// </para>
/// <para>
/// With <see cref="IFlatDbConfig.EnableLongFinality"/>, the native manager runs the persisted-snapshot tier in
/// <c>{BaseDbPath}/persistedSnapshot</c>, over the <see cref="DbNames.PersistedSnapshotCatalog"/> catalog C# opened and
/// whose rows it reads at startup, as <c>PersistedSnapshotLoader</c> does. A converted snapshot that does not read back
/// as its source exits the process as the loader does, from the pump rather than the persisting thread.
/// </para>
/// </remarks>
internal sealed partial class NativeFlatManager : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FatalReportWait = TimeSpan.FromSeconds(5);
    private const uint DetailedObservationCapacity = 16 * 1024;
    private const string PersistedSnapshotDirectory = "persistedSnapshot";

    private readonly RustPersistence _persistence;
    private readonly RustCatalogStore? _catalog;
    private readonly ManagerHandle _handle;
    private readonly ILogger _logger;
    private readonly bool _recordDetailedMetrics;
    private readonly Lock _eventLock = new();
    private readonly Lock _fatalLock = new();
    private readonly CancellationTokenSource _stopPump = new();
    private readonly CancellationTokenRegistration _processExit;
    private readonly Task _pump;
    private Task _fatalReport = Task.CompletedTask;
    private FatalErrorSource? _fatalSource;
    private string? _fatalMessage;
    private int _disposed;

    /// <param name="catalogDb">The <see cref="DbNames.PersistedSnapshotCatalog"/> database, resolved only with
    /// <see cref="IFlatDbConfig.EnableLongFinality"/>.</param>
    public unsafe NativeFlatManager(
        RustPersistence persistence,
        IFlatDbConfig config,
        IInitConfig initConfig,
        Func<IDb> catalogDb,
        CompactionSchedule schedule,
        IBlocksConfig blocksConfig,
        IStateHeaderProvider stateHeaderProvider,
        IStatePersistenceBarrier persistenceBarrier,
        IProcessExitSource processExitSource,
        IMetricsConfig metricsConfig,
        ILogManager logManager)
    {
        config.ValidateCompactSize();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.MaxInFlightCompactJob);

        _persistence = persistence;
        _logger = logManager.GetClassLogger<NativeFlatManager>();
        _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;

        // The histograms are observed only with detailed metrics, as the C# observers are no-ops otherwise; the
        // carry-forward counters follow the flag the metrics controller sets, as the C# readers do.
        bool detailedMetrics = Db.Metrics.DetailedMetricsEnabled;
        FdbMgrCfg cfg = new()
        {
            AbiVersion = FfiAbi.Version,
            MaxInFlightCompactJob = (uint)config.MaxInFlightCompactJob,
            CompactSize = config.CompactSize,
            PersistedSnapshotMaxCompactSize = config.PersistedSnapshotMaxCompactSize,
            CompactionOffset = schedule.Offset,
            MinReorgDepth = config.MinReorgDepth,
            MaxReorgDepth = config.MaxReorgDepth,
            MaxInMemorySnapshotBytes = config.MaxInMemorySnapshotBytes,
            SecondsPerSlot = blocksConfig.SecondsPerSlot,
            InMemorySlotFilterBitsPerKey = config.InMemorySnapshotBloomBitsPerKey,
            ObservationCapacity = metricsConfig.EnableDetailedMetric ? DetailedObservationCapacity : 0,
            InlineCompaction = config.InlineCompaction ? (byte)1 : (byte)0,
            PersistedNodeEvents = 1,
            DetailedMetrics = detailedMetrics ? (byte)1 : (byte)0,
        };

        RustFlatStore store = persistence.Store;
        store.AddChildReference();
        GCHandle host = default;
        try
        {
            _catalog = config.EnableLongFinality ? RustCatalogStore.Create(catalogDb()) : null;
            host = GCHandle.Alloc(new RustHostCallbacks(stateHeaderProvider, persistenceBarrier, logManager));
            FdbHost callbacks = RustHostCallbacks.ToNative(host);
            nint manager = Create(store, cfg, callbacks, config, initConfig.BaseDbPath, _catalog);
            _handle = new ManagerHandle(store, manager, host, _catalog);
        }
        catch
        {
            store.ReleaseChildReference();
            if (host.IsAllocated) host.Free();
            _catalog?.Dispose();
            throw;
        }

        _processExit = processExitSource.Token.Register(static state => ((NativeFlatManager)state!).SignalExit(), this);
        _pump = Task.Run(RunPump);
    }

    public RustPersistence Persistence => _persistence;

    public ManagerHandle Handle => _handle;

    /// <summary>The C# trie nodes of the added snapshots, marked persisted as the native manager persists them.</summary>
    public PersistedNodeReplay Replay { get; } = new();

    /// <summary>Exits the process with a code, as a failed persisted-snapshot validation does; replaced only by tests.</summary>
    internal Action<int> ExitProcess { get; set; } = Environment.Exit;

    /// <summary>Publishes the counters of the native manager and replays its histogram observations.</summary>
    public void PublishMetrics() => _handle.PublishMetrics();

    /// <summary>Throws what the C# manager would for a failed native call.</summary>
    /// <remarks>
    /// The fatal RocksDB error of the persisted-snapshot catalog, which the manager then fails every persist with, is
    /// thrown without reporting it to the flat DB, as on the C# backend only the catalog's <c>HandleFatalDbError</c>
    /// sees it. Any other RocksDB error is the flat DB's.
    /// </remarks>
    public void ThrowIfFailed(FdbStatus status)
    {
        if (status != FdbStatus.RocksDb)
        {
            _persistence.Store.ThrowIfFailed(status);
            return;
        }

        // Read first: taking a fatal error replaces this thread's last error with the fatal one.
        string message = StatusMap.LastError();
        TakeFatalError();
        if (_fatalSource == FatalErrorSource.Catalog && message == _fatalMessage) throw new RocksDbException(message);
        _persistence.Store.ThrowRocksDbError(message);
    }

    /// <summary><c>GatherReadOnlySnapshotBundle</c>: the native read-only bundle of <paramref name="baseBlock"/>.</summary>
    /// <remarks>The bundle keeps the native manager alive until it is disposed.</remarks>
    /// <exception cref="StateNotRetainedException">No snapshot ends at the state and it is not the persisted one.</exception>
    public ReadOnlySnapshotBundle GatherReadOnly(in StateId baseBlock, ReaderFlags flags) =>
        Open(baseBlock, flags, null);

    /// <summary>
    /// The read-only bundle of <paramref name="baseBlock"/> opened together with a native scope over it, which the
    /// <see cref="SnapshotBundle"/> built over the bundle takes, in one call.
    /// </summary>
    /// <inheritdoc cref="GatherReadOnly" path="/remarks"/>
    /// <inheritdoc cref="GatherReadOnly" path="/exception"/>
    public ReadOnlySnapshotBundle OpenScope(in StateId baseBlock, ReaderFlags flags, FfiScopeFlags scopeFlags) =>
        Open(baseBlock, flags, scopeFlags);

    /// <summary>Adds the snapshot a scope committed, which the native manager then shares with the caller.</summary>
    /// <returns><see cref="FdbStatus.Ok"/>, <see cref="FdbStatus.Rejected"/> or <see cref="FdbStatus.Duplicate"/>.</returns>
    public FdbStatus AddSnapshot(SnapshotHandle snapshot)
    {
        FdbStatus status = NativeMethods.fdb_add_snapshot(_handle, snapshot);
        if (status is not (FdbStatus.Ok or FdbStatus.Rejected or FdbStatus.Duplicate)) ThrowIfFailed(status);
        return status;
    }

    /// <summary>Compacts the added snapshot ending at <paramref name="to"/>, or queues its compaction, waiting for room.</summary>
    /// <returns>How long the call waited for room in the compaction queue, -1 when it did not.</returns>
    public unsafe long QueueCompaction(in StateId to)
    {
        FfiStateId nativeTo = new(to);
        long stallNs;
        ThrowIfFailed(NativeMethods.fdb_queue_compaction(_handle, &nativeTo, &stallNs));
        return stallNs;
    }

    public unsafe StateId PersistedState()
    {
        FfiStateId persisted;
        ThrowIfFailed(NativeMethods.fdb_persisted_state(_handle, &persisted));
        return persisted.ToStateId();
    }

    /// <summary>Drops from <see cref="Replay"/> the snapshots the native manager dropped without persisting them.</summary>
    public void DropUnheldReplayNodes() => Replay.DropUnheld(state => HasState(state), PersistedState);

    public unsafe bool HasState(in StateId stateId)
    {
        FfiStateId native = new(stateId);
        byte has;
        ThrowIfFailed(NativeMethods.fdb_has_state(_handle, &native, &has));
        return has != 0;
    }

    public void ClearBundleCache() => ThrowIfFailed(NativeMethods.fdb_clear_bundle_cache(_handle));

    /// <summary>Drains the native compaction and persistence queues and stops the native workers; idempotent.</summary>
    public void Shutdown() => ThrowIfFailed(NativeMethods.fdb_shutdown(_handle));

    /// <summary>Applies every persist and conversion the native manager reported to <see cref="Replay"/>, oldest first.</summary>
    public unsafe void ProcessPersistedEvents()
    {
        lock (_eventLock)
        {
            while (true)
            {
                FdbPersistedEvent persisted;
                ThrowIfFailed(NativeMethods.fdb_take_persisted_event(_handle, &persisted));
                if (persisted.Owner == 0) return;

                try
                {
                    ReadOnlySpan<FfiPersistedNode> nodes = new(persisted.Nodes, checked((int)persisted.NodeCount));
                    if (persisted.Kind == PersistedEventKind.Converted) Replay.ApplyConverted(persisted.From.ToStateId(), persisted.To.ToStateId(), nodes);
                    else Replay.Apply(persisted.From.ToStateId(), persisted.To.ToStateId(), nodes);
                }
                finally
                {
                    NativeMethods.fdb_persisted_event_release(persisted.Owner);
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _processExit.Dispose();
        try
        {
            Shutdown();
        }
        finally
        {
            _stopPump.Cancel();
            _pump.GetAwaiter().GetResult();
            PumpOnce();
            // As on the C# backend, a fatal error the last persists hit shuts the node down, writing the corruption
            // marker, before the disposal ends; the wait is bounded, as that shutdown may itself wait for the disposal.
            Task.WhenAny(_fatalReport, Task.Delay(FatalReportWait)).GetAwaiter().GetResult();
            _handle.Dispose();
            _stopPump.Dispose();
            Replay.Dispose();
        }
    }

    /// <summary>Creates the native manager, with the persisted-snapshot tier over <paramref name="catalog"/> when there is one.</summary>
    private static unsafe nint Create(RustFlatStore store, FdbMgrCfg cfg, FdbHost callbacks, IFlatDbConfig config, string baseDbPath, RustCatalogStore? catalog)
    {
        nint manager;
        if (catalog is null)
        {
            store.ThrowIfFailed(NativeMethods.fdb_mgr_create(store, &cfg, &callbacks, &manager));
            return manager;
        }

        string basePath = Path.Combine(baseDbPath, PersistedSnapshotDirectory);
        byte[] arenaDirectory = Encoding.UTF8.GetBytes(Path.Combine(basePath, "arena"));
        byte[] blobDirectory = Encoding.UTF8.GetBytes(Path.Combine(basePath, "blob"));
        byte[] catalogRows = catalog.Rows();
        ManagedCatalogVTable catalogCallbacks;
        fixed (byte* arena = arenaDirectory, blob = blobDirectory, rows = catalogRows)
        {
            FdbTierCfg tier = new()
            {
                ArenaDir = arena,
                ArenaDirLength = (nuint)arenaDirectory.Length,
                BlobDir = blob,
                BlobDirLength = (nuint)blobDirectory.Length,
                Catalog = catalog.Descriptor(&catalogCallbacks),
                CatalogRows = rows,
                CatalogRowsLength = (nuint)catalogRows.Length,
                LongFinalityMaxReorgDepth = config.LongFinalityMaxReorgDepth,
                MaxInMemoryBaseSnapshotCount = (ulong)Math.Max(0, config.MaxInMemoryBaseSnapshotCount),
                ArenaFileSizeBytes = config.ArenaFileSizeBytes,
                DedicatedArenaThresholdBytes = config.PersistedSnapshotDedicatedArenaThresholdBytes,
                ArenaPageCacheBytes = config.PersistedSnapshotArenaPageCacheBytes,
                BloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey,
                ProcessorCount = (uint)Environment.ProcessorCount,
                PunchHoleOnReclaim = config.PersistedSnapshotPunchHoleOnReclaim ? (byte)1 : (byte)0,
                ValidatePersistedSnapshot = config.ValidatePersistedSnapshot ? (byte)1 : (byte)0,
            };
            cfg.Tier = &tier;
            store.ThrowIfFailed(NativeMethods.fdb_mgr_create(store, &cfg, &callbacks, &manager));
            return manager;
        }
    }

    /// <summary>A read-only bundle of <paramref name="baseBlock"/>, over a scope opened with it when <paramref name="scopeFlags"/> is given.</summary>
    private unsafe ReadOnlySnapshotBundle Open(in StateId baseBlock, ReaderFlags flags, FfiScopeFlags? scopeFlags)
    {
        RustFlatStore store = _persistence.Store;
        FfiRobInfo info;
        BundleBaseHandle handle;
        store.AddChildReference();
        bool managerAdded = false;
        try
        {
            _handle.DangerousAddRef(ref managerAdded);
            FfiStateId nativeBase = new(baseBlock);
            nint bundle;
            FdbStatus status;
            if (scopeFlags is { } openedScope)
            {
                status = NativeMethods.fdb_scope_open(_handle, &nativeBase, (uint)flags, &openedScope, &bundle, &info);
            }
            else
            {
                status = NativeMethods.fdb_rob_gather(_handle, &nativeBase, (uint)flags, &bundle, &info);
            }

            if (status == FdbStatus.StateNotRetained)
            {
                throw new StateNotRetainedException($"No state available for block {baseBlock.BlockNumber} with state root {baseBlock.StateRoot}");
            }

            ThrowIfFailed(status);
            // The references on the store and the manager pass to the handle.
            handle = scopeFlags is null ? new RobHandle(store, _handle, bundle) : new ScopeHandle(store, _handle, bundle);
        }
        catch
        {
            if (managerAdded) _handle.DangerousRelease();
            store.ReleaseChildReference();
            throw;
        }

        return new ReadOnlySnapshotBundle(handle, info, _recordDetailedMetrics);
    }

    [GeneratedRegex(@"broken\.(\d+)\.(\d+)\.json")]
    private static partial Regex DumpFileRange();

    private void SignalExit()
    {
        try
        {
            ThrowIfFailed(NativeMethods.fdb_signal_exit(_handle));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunPump()
    {
        using PeriodicTimer timer = new(PumpInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopPump.Token)) PumpOnce();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PumpOnce()
    {
        try
        {
            ProcessPersistedEvents();
            _handle.PublishMetrics();
            _persistence.Store.PumpLogs();
            TakeFatalError();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Processing the native flat DB manager's events failed.", exception);
        }
    }

    /// <summary>
    /// Takes the fatal error the native manager stored, if it stored one since, and reports it to the database it hit,
    /// or exits the process on a failed validation.
    /// </summary>
    /// <remarks>Remembers the fatal error the manager fails its persists with in <see cref="_fatalSource"/> and <see cref="_fatalMessage"/>.</remarks>
    private unsafe void TakeFatalError()
    {
        lock (_fatalLock)
        {
            FatalErrorSource source;
            if (NativeMethods.fdb_take_fatal_error(_handle, &source) == FdbStatus.Ok) return;

            _fatalSource = source;
            _fatalMessage = StatusMap.LastError();
            Report(source, _fatalMessage);
        }
    }

    private void Report(FatalErrorSource source, string message)
    {
        if (source == FatalErrorSource.Validation)
        {
            // The dump file names the range, as PersistedSnapshotUtils names it; an error that left no dump names none.
            Match range = DumpFileRange().Match(message);
            string snapshot = range.Success ? $"range {range.Groups[1].Value}..{range.Groups[2].Value}" : "a converted snapshot";
            if (_logger.IsError) _logger.Error($"Persisted snapshot validation failed for {snapshot}. Exiting with code {ExitCodes.GeneralError} for git bisect compatibility.", new InvalidOperationException(message));
        }
        else if (_logger.IsError)
        {
            _logger.Error($"Persisting the flat DB failed fatally: {message}");
        }

        // On a pool thread, as shutting down on the error disposes the node, which waits for the pump.
        _fatalReport = source switch
        {
            FatalErrorSource.Validation => Task.Run(() => ExitProcess(ExitCodes.GeneralError)),
            FatalErrorSource.Catalog => Task.Run(() => _catalog?.ReportFatal(message)),
            _ => Task.Run(() => _persistence.Store.ReportFatal(message)),
        };
    }
}
