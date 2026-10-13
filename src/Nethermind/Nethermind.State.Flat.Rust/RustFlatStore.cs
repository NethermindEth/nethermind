// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat.Rust.Interop;
using FlatMetrics = Nethermind.State.Flat.Metrics;

namespace Nethermind.State.Flat.Rust;

/// <summary>The native flat DB store: the persistence stack of the native library, on the database of the flat columns.</summary>
/// <remarks>
/// Over a RocksDB <see cref="ColumnsDb{T}"/> the store drives the very instance C# opened, through the
/// <see cref="RocksApiTable"/>, and holds a native lease on it until the handle is released, so disposing the database
/// waits for the store rather than closing under it. Any other database is reached through the
/// <see cref="ManagedKvBridge"/>. Every reader, iterator and batch handle holds a reference on the store, so the native
/// store, which refuses to be destroyed while one is open, is destroyed once the last of them is released. What the
/// store logs goes through its <see cref="LogPump"/>.
/// </remarks>
internal sealed unsafe class RustFlatStore : SafeHandle
{
    private static readonly int ColumnCount = Enum.GetValues<FlatDbColumns>().Length;

    private readonly ColumnsDb<FlatDbColumns>? _rocksDb;
    private readonly ManagedKvBridge? _bridge;
    private readonly LogPump _logPump;
    private readonly bool _carryForward;

    private RustFlatStore(nint store, in FdbStoreInfo info, ColumnsDb<FlatDbColumns>? rocksDb, ManagedKvBridge? bridge, LogPump logPump, bool carryForward)
        : base(0, ownsHandle: true)
    {
        SetHandle(store);
        _rocksDb = rocksDb;
        _bridge = bridge;
        _logPump = logPump;
        _carryForward = carryForward;
        RlpWrapSlots = info.RlpWrapSlots != 0;
        IsPreimage = info.Preimage != 0;
    }

    public override bool IsInvalid => handle == 0;

    /// <summary>Whether slot values are RLP-wrapped rather than in the deprecated raw encoding.</summary>
    public bool RlpWrapSlots { get; }

    /// <summary>Whether the layout keys flat state by address and slot rather than their hashes.</summary>
    public bool IsPreimage { get; }

    /// <summary>The managed backend, when the database is not RocksDB.</summary>
    internal ManagedKvBridge? Bridge => _bridge;

    /// <summary>Creates the store the configuration describes over <paramref name="db"/>.</summary>
    /// <exception cref="InvalidOperationException">The native library is missing or incompatible.</exception>
    public static RustFlatStore Create(IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
    {
        NativeMethods.EnsureAvailable();
        SeamCounters.ApplyTimingVariable();
        LogPump logPump = new(logManager, config.Layout);
        FdbStoreCfg cfg = new()
        {
            AbiVersion = FfiAbi.Version,
            Layout = (uint)config.Layout,
            WriteBufferFloor = config.PersistenceWriteBufferFloor,
            CarryForward = config.EnableCarryForwardCache ? (byte)1 : (byte)0,
            LogLevel = logPump.Level,
        };

        RustFlatStore store;
        if (db is ColumnsDb<FlatDbColumns> rocksDb)
        {
            store = CreateOnRocksDb(rocksDb, cfg, logPump);
        }
        else
        {
            ILogger logger = logManager.GetClassLogger<RustFlatStore>();
            if (logger.IsWarn) logger.Warn($"FlatDb.Backend={config.Backend} runs on {db.GetType().Name}, which is not RocksDB, through callbacks into C#: results are not valid for benchmarks.");
            store = CreateOnManagedKv(db, cfg, logPump);
        }

        // Resolving the slot encoding can warn.
        store.PumpLogs();
        return store;
    }

    private static RustFlatStore CreateOnRocksDb(ColumnsDb<FlatDbColumns> db, FdbStoreCfg cfg, LogPump logPump)
    {
        RocksApiTable api = RocksApiTable.Shared;
        db.AcquireNativeLease();
        try
        {
            nint* columnFamilies = stackalloc nint[ColumnCount];
            for (int i = 0; i < ColumnCount; i++)
            {
                columnFamilies[i] = ((ColumnDb)db.GetColumnDb((FlatDbColumns)i))._columnFamily.Handle;
            }

            FdbKvDesc kv = new()
            {
                Kind = FdbKvKind.Rocks,
                ColumnCount = (uint)ColumnCount,
                RocksApi = api.Pointer,
                RocksDb = db._db.Handle,
                RocksColumnFamilies = columnFamilies,
                ReadaheadSize = db.ReadAheadSize,
                PrefixExtractorLength = (uint)db.PrefixExtractorLength,
                VerifyChecksums = db.VerifyChecksum ? (byte)1 : (byte)0,
                WalSync = db.WriteOptions!.GetSync() ? (byte)1 : (byte)0,
            };

            nint store;
            FdbStoreInfo info;
            ThrowIfFailed(NativeMethods.fdb_store_create(&cfg, &kv, &store, &info), db);
            return new RustFlatStore(store, info, db, null, logPump, cfg.CarryForward != 0);
        }
        catch
        {
            db.ReleaseNativeLease();
            throw;
        }
    }

    private static RustFlatStore CreateOnManagedKv(IColumnsDb<FlatDbColumns> db, FdbStoreCfg cfg, LogPump logPump)
    {
        ManagedKvBridge bridge = new(db);
        try
        {
            ManagedKvVTable vtable = bridge.VTable;
            FdbKvDesc kv = new() { Kind = FdbKvKind.Managed, Managed = &vtable };

            nint store;
            FdbStoreInfo info;
            ThrowIfFailed(NativeMethods.fdb_store_create(&cfg, &kv, &store, &info), null);
            return new RustFlatStore(store, info, null, bridge, logPump, cfg.CarryForward != 0);
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    protected override bool ReleaseHandle()
    {
        // The reads of the handle released last, after the persistence was disposed, reach the metrics too.
        FdbStats stats;
        if (NativeMethods.fdb_released_store_take_stats(handle, &stats) == FdbStatus.Ok) Publish(stats);
        if (NativeMethods.fdb_store_destroy(handle) != FdbStatus.Ok) return false;

        _rocksDb?.ReleaseNativeLease();
        _bridge?.Dispose();
        return true;
    }

    /// <summary>Throws what the C# persistence would for a failed call on the store.</summary>
    public void ThrowIfFailed(FdbStatus status) => ThrowIfFailed(status, _rocksDb);

    /// <summary>Lets <see cref="DbOnTheRocks.HandleFatalDbError"/> shut the node down on a RocksDB error the native library hit off any call.</summary>
    public void ReportFatal(string message) => _rocksDb?.HandleFatalDbError(new RocksDbException(message));

    /// <remarks>
    /// A RocksDB failure becomes the <see cref="RocksDbException"/> the bindings throw, after
    /// <see cref="DbOnTheRocks.HandleFatalDbError"/> had its say, as every <see cref="DbOnTheRocks"/> call does.
    /// </remarks>
    private static void ThrowIfFailed(FdbStatus status, DbOnTheRocks? rocksDb)
    {
        if (status == FdbStatus.Ok) return;
        if (status == FdbStatus.RocksDb && rocksDb is not null)
        {
            RocksDbException exception = new(StatusMap.LastError());
            rocksDb.HandleFatalDbError(exception);
            throw exception;
        }

        StatusMap.Throw(status);
    }

    /// <summary>Takes a reference on the store for a handle about to be created; the handle releases it.</summary>
    /// <exception cref="ObjectDisposedException">The store is disposed.</exception>
    public void AddChildReference()
    {
        bool added = false;
        DangerousAddRef(ref added);
    }

    /// <summary>Gives back a reference <see cref="AddChildReference"/> took for a handle that was never created.</summary>
    public void ReleaseChildReference() => DangerousRelease();

    /// <summary>Logs what the store, and the workers of its manager, logged since the last call; nothing once released.</summary>
    public void PumpLogs()
    {
        try
        {
            _logPump.Drain(this);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Adds the metrics the store gathered since the last call to their C# counterparts: the reads to the
    /// <c>TotalReads</c> metric of the database, and the carry-forward counters and gauges to
    /// <see cref="FlatMetrics"/>.
    /// </summary>
    public void PublishMetrics()
    {
        if (IsClosed) return;

        FdbStats stats;
        try
        {
            ThrowIfFailed(NativeMethods.fdb_store_take_stats(this, &stats));
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Publish(stats);
    }

    private void Publish(in FdbStats stats)
    {
        if (stats.Reads != 0) _rocksDb?.UpdateReadMetrics((int)Math.Min(stats.Reads, int.MaxValue));
        if (!_carryForward) return;

        Repeat(stats.CarryForwardAccountHits, FlatMetrics.IncrementCarryForwardAccountHits);
        Repeat(stats.CarryForwardAccountMisses, FlatMetrics.IncrementCarryForwardAccountMisses);
        Repeat(stats.CarryForwardSlotHits, FlatMetrics.IncrementCarryForwardSlotHits);
        Repeat(stats.CarryForwardSlotMisses, FlatMetrics.IncrementCarryForwardSlotMisses);
        Repeat(stats.CarryForwardAccountWipes, FlatMetrics.IncrementCarryForwardAccountWipes);
        Repeat(stats.CarryForwardSlotEvictions, FlatMetrics.IncrementCarryForwardSlotEvictions);
        FlatMetrics.PublishCarryForwardAccountCount(stats.CarryForwardAccountCount);
        FlatMetrics.PublishCarryForwardSlotCount(stats.CarryForwardSlotCount);

        // The C# counters only increment.
        static void Repeat(long count, Action increment)
        {
            for (long i = 0; i < count; i++) increment();
        }
    }
}
