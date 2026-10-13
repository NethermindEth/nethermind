// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>
/// Logs what a native store logged: each record through the logger of the C# class whose log line it is, at its level,
/// so the lines of the Rust backend come out as those of the C# one.
/// </summary>
/// <remarks>
/// The records wait in the store's log sink until drained: the store drains after the calls that can log, and the
/// native manager's pump drains what its worker threads logged. Records logged outside any store, which no call should
/// do, are drained along with each store's.
/// </remarks>
internal sealed unsafe class LogPump
{
    private const int BufferSize = 4096;

    private readonly ILogger[] _loggers;
    private readonly Lock _lock = new();

    public LogPump(ILogManager logManager, FlatLayout layout)
    {
        _loggers =
        [
            logManager.GetClassLogger<RustFlatStore>(),
            logManager.GetClassLogger<FlatDbManager>(),
            logManager.GetClassLogger<PersistenceManager>(),
            logManager.GetClassLogger<SnapshotRepository>(),
            logManager.GetClassLogger<SnapshotCompactor>(),
            logManager.GetClassLogger<ReadOnlySnapshotBundle>(),
            layout switch
            {
                FlatLayout.Flat => logManager.GetClassLogger<RocksDbPersistence>(),
                FlatLayout.FlatInTrie => logManager.GetClassLogger<FlatInTriePersistence>(),
                _ => logManager.GetClassLogger<PreimageRocksdbPersistence>(),
            },
            logManager.GetClassLogger<DbOnTheRocks>(),
        ];
        Level = _loggers.Max(LevelOf);
    }

    /// <summary>The most verbose level any of the loggers logs, which the store's sink keeps.</summary>
    public NativeLogLevel Level { get; }

    /// <summary>Logs every record <paramref name="store"/> logged, then those logged outside any store.</summary>
    public void Drain(RustFlatStore store)
    {
        byte* buffer = stackalloc byte[BufferSize];
        lock (_lock)
        {
            while (TakeOne(store, buffer)) { }
            while (TakeOne(null, buffer)) { }

            ulong dropped = NativeMethods.fdb_store_log_dropped(store) + NativeMethods.fdb_unrouted_log_dropped(0);
            ILogger logger = _loggers[(int)NativeLogSource.Other];
            if (dropped != 0 && logger.IsWarn) logger.Warn($"{dropped} log records of the native flat DB were dropped: the log ring was full.");
        }
    }

    private bool TakeOne(RustFlatStore? store, byte* buffer)
    {
        NativeLogSource source;
        NativeLogLevel level;
        nuint length;
        byte has;
        StatusMap.ThrowIfFailed(store is null
            ? NativeMethods.fdb_unrouted_log_take(0, &source, &level, buffer, BufferSize, &length, &has)
            : NativeMethods.fdb_store_log_take(store, &source, &level, buffer, BufferSize, &length, &has));
        if (has == 0) return false;

        ILogger logger = (uint)source < (uint)_loggers.Length ? _loggers[(int)source] : _loggers[(int)NativeLogSource.Other];
        string message = Encoding.UTF8.GetString(buffer, (int)Math.Min(length, BufferSize));
        switch (level)
        {
            case NativeLogLevel.Error: if (logger.IsError) logger.Error(message); break;
            case NativeLogLevel.Warn: if (logger.IsWarn) logger.Warn(message); break;
            case NativeLogLevel.Info: if (logger.IsInfo) logger.Info(message); break;
            case NativeLogLevel.Debug: if (logger.IsDebug) logger.Debug(message); break;
            default: if (logger.IsTrace) logger.Trace(message); break;
        }

        return true;
    }

    private static NativeLogLevel LevelOf(ILogger logger) =>
        logger.IsTrace ? NativeLogLevel.Trace
        : logger.IsDebug ? NativeLogLevel.Debug
        : logger.IsInfo ? NativeLogLevel.Info
        : logger.IsWarn ? NativeLogLevel.Warn
        : logger.IsError ? NativeLogLevel.Error
        : NativeLogLevel.Off;
}
