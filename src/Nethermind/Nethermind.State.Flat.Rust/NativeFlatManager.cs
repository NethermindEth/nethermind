// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// The native snapshot manager — <c>FlatDbManager</c> and <c>PersistenceManager</c> in the Rust library — over the store
/// of <see cref="RustPersistence"/>, shared by <see cref="RustFlatDbManager"/> and <see cref="RustPersistenceManager"/>.
/// </summary>
/// <remarks>
/// It calls back into the services <c>PersistenceManager</c> injects through <see cref="RustHostCallbacks"/>, and
/// signals process exit to the native manager as <c>FlatDbManager</c> watches <see cref="IProcessExitSource.Token"/>.
/// A background loop applies the persisted-node events to <see cref="Replay"/>, publishes the metrics, logs what the
/// library logged and reports a fatal RocksDB error a native persist hit, as <see cref="RustFlatStore.ThrowIfFailed"/>
/// reports one an export hit. Disposing it drains and stops the native workers, then frees the manager; the store and
/// its database stay open until then, as the manager holds a reference on the store.
/// </remarks>
internal sealed class NativeFlatManager : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);
    private const int LogBufferSize = 4096;
    private const uint DetailedObservationCapacity = 16 * 1024;

    private readonly RustPersistence _persistence;
    private readonly ManagerHandle _handle;
    private readonly RustMetricsBridge _metrics;
    private readonly ILogger _logger;
    private readonly Lock _eventLock = new();
    private readonly CancellationTokenSource _stopPump = new();
    private readonly CancellationTokenRegistration _processExit;
    private readonly Task _pump;
    private int _disposed;

    public unsafe NativeFlatManager(
        RustPersistence persistence,
        IFlatDbConfig config,
        CompactionSchedule schedule,
        IBlocksConfig blocksConfig,
        IStateHeaderProvider stateHeaderProvider,
        IStatePersistenceBarrier persistenceBarrier,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        config.ValidateCompactSize();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(config.MaxInFlightCompactJob);

        _persistence = persistence;
        _logger = logManager.GetClassLogger<NativeFlatManager>();
        InstallLogging(_logger);

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
            ObservationCapacity = detailedMetrics ? DetailedObservationCapacity : 0,
            InlineCompaction = config.InlineCompaction ? (byte)1 : (byte)0,
            PersistedNodeEvents = 1,
            DetailedMetrics = detailedMetrics ? (byte)1 : (byte)0,
        };

        GCHandle host = GCHandle.Alloc(new RustHostCallbacks(stateHeaderProvider, persistenceBarrier, logManager));
        RustFlatStore store = persistence.Store;
        store.AddChildReference();
        try
        {
            FdbHost callbacks = RustHostCallbacks.ToNative(host);
            nint manager;
            store.ThrowIfFailed(NativeMethods.fdb_mgr_create(store, &cfg, &callbacks, &manager));
            _handle = new ManagerHandle(store, manager, host);
        }
        catch
        {
            store.ReleaseChildReference();
            host.Free();
            throw;
        }

        _metrics = new RustMetricsBridge(_handle);
        _processExit = processExitSource.Token.Register(static state => ((NativeFlatManager)state!).SignalExit(), this);
        _pump = Task.Run(RunPump);
    }

    public RustPersistence Persistence => _persistence;

    public ManagerHandle Handle => _handle;

    /// <summary>The C# trie nodes of the added snapshots, marked persisted as the native manager persists them.</summary>
    public PersistedNodeReplay Replay { get; } = new();

    /// <summary>Throws what the C# manager would for a failed native call.</summary>
    public void ThrowIfFailed(FdbStatus status) => _persistence.Store.ThrowIfFailed(status);

    /// <summary>A reader of <paramref name="baseBlock"/> through the in-memory snapshots, then the persistence.</summary>
    /// <exception cref="StateNotRetainedException">No snapshot ends at the state and it is not the persisted one.</exception>
    public unsafe IPersistence.IPersistenceReader Gather(in StateId baseBlock, ReaderFlags flags, bool filtered)
    {
        RustFlatStore store = _persistence.Store;
        store.AddChildReference();
        try
        {
            FfiStateId nativeBase = new(baseBlock);
            nint reader;
            FfiStateId readerState;
            FdbStatus status = NativeMethods.fdb_gather(_handle, &nativeBase, (uint)flags, filtered ? (byte)1 : (byte)0, &reader, &readerState);
            if (status == FdbStatus.StateNotRetained)
            {
                throw new StateNotRetainedException($"No state available for block {baseBlock.BlockNumber} with state root {baseBlock.StateRoot}");
            }

            ThrowIfFailed(status);
            return _persistence.WrapReader(reader, readerState.ToStateId());
        }
        catch
        {
            store.ReleaseChildReference();
            throw;
        }
    }

    /// <summary>Adds the snapshot <paramref name="builder"/> holds, consuming the builder whatever the outcome.</summary>
    /// <returns><see cref="FdbStatus.Ok"/>, <see cref="FdbStatus.Rejected"/> or <see cref="FdbStatus.Duplicate"/>.</returns>
    public FdbStatus AddSnapshot(nint builder)
    {
        FdbStatus status = NativeMethods.fdb_add_snapshot(_handle, builder);
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

    /// <summary>Applies every persist the native manager reported to <see cref="Replay"/>, oldest first.</summary>
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
                    Replay.Apply(persisted.From.ToStateId(), persisted.To.ToStateId(), new ReadOnlySpan<FfiPersistedNode>(persisted.Nodes, checked((int)persisted.NodeCount)));
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
            _handle.Dispose();
            _stopPump.Dispose();
            Replay.Dispose();
        }
    }

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
            _metrics.Publish();
            PumpLogs();
            ReportFatalError();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Processing the native flat DB manager's events failed.", exception);
        }
    }

    private void ReportFatalError()
    {
        if (NativeMethods.fdb_take_fatal_error(_handle) == FdbStatus.Ok) return;

        string message = StatusMap.LastError();
        if (_logger.IsError) _logger.Error($"Persisting the flat DB failed fatally: {message}");
        // Off the pump, which disposal waits for, as shutting down on the error disposes the node.
        _ = Task.Run(() => _persistence.Store.ReportFatal(message));
    }

    private static void InstallLogging(ILogger logger)
    {
        uint level = logger.IsTrace ? 5u : logger.IsDebug ? 4u : logger.IsInfo ? 3u : logger.IsWarn ? 2u : logger.IsError ? 1u : 0u;
        StatusMap.ThrowIfFailed(NativeMethods.fdb_log_install(level));
    }

    /// <summary>Logs what the library logged, through this manager's logger: the ring is shared by the whole process.</summary>
    private unsafe void PumpLogs()
    {
        byte* buffer = stackalloc byte[LogBufferSize];
        while (true)
        {
            uint level;
            nuint length;
            byte has;
            StatusMap.ThrowIfFailed(NativeMethods.fdb_log_take(&level, buffer, LogBufferSize, &length, &has));
            if (has == 0) break;

            string message = Encoding.UTF8.GetString(buffer, (int)Math.Min(length, LogBufferSize));
            switch (level)
            {
                case 1: if (_logger.IsError) _logger.Error(message); break;
                case 2: if (_logger.IsWarn) _logger.Warn(message); break;
                case 3: if (_logger.IsInfo) _logger.Info(message); break;
                case 4: if (_logger.IsDebug) _logger.Debug(message); break;
                default: if (_logger.IsTrace) _logger.Trace(message); break;
            }
        }

        ulong dropped = NativeMethods.fdb_log_dropped();
        if (dropped != 0 && _logger.IsWarn) _logger.Warn($"{dropped} log records of the native flat DB were dropped: the log ring was full.");
    }
}
