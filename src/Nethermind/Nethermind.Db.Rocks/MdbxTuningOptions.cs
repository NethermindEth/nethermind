// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Numerics;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

internal readonly record struct MdbxTuningOptions(
    long InitialMapSize,
    long MaxMapSize,
    long GrowthStep,
    long ShrinkThreshold,
    int PageSize,
    ulong MaxDbs,
    ulong MaxReaders,
    ulong RpAugmentLimit,
    ulong DirtyPagesReserveLimit,
    ulong TransactionDirtyPagesLimit,
    ulong TransactionDirtyPagesInitial,
    long SyncBytes,
    int SyncPeriodSeconds,
    bool EnableReadAhead,
    bool EnableWriteMap,
    bool EnableCoalesce,
    bool EnableBatchGrouping,
    bool EnableAppend,
    MdbxDisableWalSyncMode DisableWalSyncMode,
    int MaxBatchGroupOperations,
    long MaxBatchGroupBytes,
    bool EnableProfiling,
    int HotPathSampleRate,
    TimeSpan ProfileInterval,
    TimeSpan SlowTransactionThreshold,
    bool HasDirtyPagesReserveLimitOverride,
    bool HasTransactionDirtyPagesLimitOverride,
    bool HasTransactionDirtyPagesInitialOverride,
    bool HasOverrides)
{
    public const long DefaultInitialMapSize = 64L << 20;
    public const long DefaultMaxMapSize = 1L << 40;
    public const long DefaultGrowthStep = 1L << 30;
    public const long DefaultShrinkThreshold = 1L << 30;
    public const int DefaultPageSize = 4 * 1024;
    public const ulong DefaultMaxDbs = 512;
    public const ulong DefaultMaxReaders = 8192;
    public const ulong DefaultRpAugmentLimit = 256 * 1024;
    public const ulong DefaultStateRpAugmentLimit = 1_000_000_000;
    public const int DefaultMaxBatchGroupOperations = 128 * 1024;
    public const int DefaultStateMaxBatchGroupOperations = 16 * 1024;
    public const long DefaultMaxBatchGroupBytes = 64L << 20;
    public const long DefaultStateDirtyPagesReserveBytes = 1L << 30;
    public const long DefaultStateTransactionDirtyPagesLimitBytes = 1L << 30;
    public const long DefaultStateTransactionDirtyPagesInitialBytes = 128L << 20;
    public const MdbxDisableWalSyncMode DefaultDisableWalSyncMode = MdbxDisableWalSyncMode.SafeNoSync;
    public const long DefaultSafeNoSyncSyncBytes = 1L << 30;
    public const int DefaultSafeNoSyncSyncPeriodSeconds = 30;
    public const int MaxSyncPeriodSeconds = ushort.MaxValue;

    private const int DefaultProfileIntervalSeconds = 30;
    private const int DefaultSlowTransactionMilliseconds = 1_000;
    private const int DefaultHotPathSampleRate = 128;

    public static MdbxTuningOptions ReadFromEnvironment(ILogger logger, string? path = null)
    {
        bool hasOverrides = false;
        long initialMapSize = ReadSize("NETHERMIND_MDBX_INITIAL_MAP_SIZE", DefaultInitialMapSize, logger, ref hasOverrides);
        long maxMapSize = ReadSize("NETHERMIND_MDBX_MAX_MAP_SIZE", DefaultMaxMapSize, logger, ref hasOverrides);
        long growthStep = ReadSize("NETHERMIND_MDBX_GROWTH_STEP", DefaultGrowthStep, logger, ref hasOverrides);
        long shrinkThreshold = ReadSize("NETHERMIND_MDBX_SHRINK_THRESHOLD", DefaultShrinkThreshold, logger, ref hasOverrides);
        int pageSize = ReadInt32("NETHERMIND_MDBX_PAGE_SIZE", DefaultPageSize, logger, ref hasOverrides);
        ulong maxDbs = ReadUInt64("NETHERMIND_MDBX_MAX_DBS", DefaultMaxDbs, logger, ref hasOverrides);
        ulong maxReaders = ReadUInt64("NETHERMIND_MDBX_MAX_READERS", DefaultMaxReaders, logger, ref hasOverrides);
        bool enableReadAhead = ReadBool("NETHERMIND_MDBX_READAHEAD", fallback: false, logger, ref hasOverrides);
        bool enableWriteMap = ReadBool("NETHERMIND_MDBX_WRITEMAP", fallback: true, logger, ref hasOverrides);
        bool enableCoalesce = ReadBool("NETHERMIND_MDBX_COALESCE", fallback: true, logger, ref hasOverrides);
        bool enableBatchGrouping = ReadBool("NETHERMIND_MDBX_BATCH_GROUP", fallback: true, logger, ref hasOverrides);
        bool enableAppend = ReadBool("NETHERMIND_MDBX_APPEND", fallback: true, logger, ref hasOverrides);
        MdbxDisableWalSyncMode disableWalSyncMode = ReadDisableWalSyncMode("NETHERMIND_MDBX_DISABLE_WAL_SYNC_MODE", DefaultDisableWalSyncMode, logger, ref hasOverrides);
        long defaultSyncBytes = disableWalSyncMode == MdbxDisableWalSyncMode.SafeNoSync ? DefaultSafeNoSyncSyncBytes : 0;
        int defaultSyncPeriodSeconds = disableWalSyncMode == MdbxDisableWalSyncMode.SafeNoSync ? DefaultSafeNoSyncSyncPeriodSeconds : 0;
        long syncBytes = ReadSize("NETHERMIND_MDBX_SYNC_BYTES", defaultSyncBytes, logger, ref hasOverrides);
        int syncPeriodSeconds = ReadInt32("NETHERMIND_MDBX_SYNC_PERIOD_SECONDS", defaultSyncPeriodSeconds, logger, ref hasOverrides);
        bool enableProfiling = ReadBool("NETHERMIND_MDBX_PROFILE", fallback: false, logger, ref hasOverrides);
        int hotPathSampleRate = ReadInt32("NETHERMIND_MDBX_PROFILE_HOTPATH_SAMPLE_RATE", DefaultHotPathSampleRate, logger, ref hasOverrides);
        int profileIntervalSeconds = ReadInt32("NETHERMIND_MDBX_PROFILE_INTERVAL_SECONDS", DefaultProfileIntervalSeconds, logger, ref hasOverrides);
        int slowTransactionMilliseconds = ReadInt32("NETHERMIND_MDBX_SLOW_TRANSACTION_MS", DefaultSlowTransactionMilliseconds, logger, ref hasOverrides);

        if (initialMapSize <= 0)
        {
            Warn(logger, "NETHERMIND_MDBX_INITIAL_MAP_SIZE must be positive. Using the default value.");
            initialMapSize = DefaultInitialMapSize;
        }

        if (maxMapSize < initialMapSize)
        {
            Warn(logger, "NETHERMIND_MDBX_MAX_MAP_SIZE is smaller than the initial map size. Using the default maximum map size.");
            maxMapSize = Math.Max(DefaultMaxMapSize, initialMapSize);
        }

        if (growthStep <= 0)
        {
            Warn(logger, "NETHERMIND_MDBX_GROWTH_STEP must be positive. Using the default value.");
            growthStep = DefaultGrowthStep;
        }

        if (shrinkThreshold < 0)
        {
            Warn(logger, "NETHERMIND_MDBX_SHRINK_THRESHOLD must be zero or positive. Using the default value.");
            shrinkThreshold = DefaultShrinkThreshold;
        }

        if (!IsValidPageSize(pageSize))
        {
            Warn(logger, "NETHERMIND_MDBX_PAGE_SIZE must be a power of two between 4096 and 65536. Using the default value.");
            pageSize = DefaultPageSize;
        }

        bool isStateDb = MdbxPathHelpers.IsStateDbPath(path);
        ulong defaultRpAugmentLimit = isStateDb ? DefaultStateRpAugmentLimit : DefaultRpAugmentLimit;
        ulong defaultDirtyPagesReserveLimit = isStateDb ? BytesToPages(DefaultStateDirtyPagesReserveBytes, pageSize) : 0;
        ulong defaultTransactionDirtyPagesLimit = isStateDb ? BytesToPages(DefaultStateTransactionDirtyPagesLimitBytes, pageSize) : 0;
        ulong defaultTransactionDirtyPagesInitial = isStateDb ? BytesToPages(DefaultStateTransactionDirtyPagesInitialBytes, pageSize) : 0;
        int defaultMaxBatchGroupOperations = isStateDb ? DefaultStateMaxBatchGroupOperations : DefaultMaxBatchGroupOperations;

        ulong rpAugmentLimit = ReadUInt64("NETHERMIND_MDBX_RP_AUGMENT_LIMIT", defaultRpAugmentLimit, logger, ref hasOverrides);
        ulong dirtyPagesReserveLimit = ReadUInt64("NETHERMIND_MDBX_DIRTY_PAGES_RESERVE_LIMIT", defaultDirtyPagesReserveLimit, logger, ref hasOverrides, out bool hasDirtyPagesReserveLimitOverride);
        ulong transactionDirtyPagesLimit = ReadUInt64("NETHERMIND_MDBX_TXN_DIRTY_PAGES_LIMIT", defaultTransactionDirtyPagesLimit, logger, ref hasOverrides, out bool hasTransactionDirtyPagesLimitOverride);
        ulong transactionDirtyPagesInitial = ReadUInt64("NETHERMIND_MDBX_TXN_DIRTY_PAGES_INITIAL", defaultTransactionDirtyPagesInitial, logger, ref hasOverrides, out bool hasTransactionDirtyPagesInitialOverride);
        int maxBatchGroupOperations = ReadInt32("NETHERMIND_MDBX_BATCH_GROUP_MAX_OPS", defaultMaxBatchGroupOperations, logger, ref hasOverrides);
        long maxBatchGroupBytes = ReadSize("NETHERMIND_MDBX_BATCH_GROUP_MAX_BYTES", DefaultMaxBatchGroupBytes, logger, ref hasOverrides);

        if (maxDbs == 0)
        {
            Warn(logger, "NETHERMIND_MDBX_MAX_DBS must be positive. Using the default value.");
            maxDbs = DefaultMaxDbs;
        }

        if (maxReaders == 0)
        {
            Warn(logger, "NETHERMIND_MDBX_MAX_READERS must be positive. Using the default value.");
            maxReaders = DefaultMaxReaders;
        }

        if (maxBatchGroupOperations <= 0)
        {
            Warn(logger, "NETHERMIND_MDBX_BATCH_GROUP_MAX_OPS must be positive. Using the default value.");
            maxBatchGroupOperations = defaultMaxBatchGroupOperations;
        }

        if (maxBatchGroupBytes <= 0)
        {
            Warn(logger, "NETHERMIND_MDBX_BATCH_GROUP_MAX_BYTES must be positive. Using the default value.");
            maxBatchGroupBytes = DefaultMaxBatchGroupBytes;
        }

        if (syncBytes < 0)
        {
            Warn(logger, "NETHERMIND_MDBX_SYNC_BYTES must be zero or positive. Using the default value.");
            syncBytes = defaultSyncBytes;
        }

        if (syncPeriodSeconds is < 0 or > MaxSyncPeriodSeconds)
        {
            Warn(logger, $"NETHERMIND_MDBX_SYNC_PERIOD_SECONDS must be between 0 and {MaxSyncPeriodSeconds}. Using the default value.");
            syncPeriodSeconds = defaultSyncPeriodSeconds;
        }

        if (hotPathSampleRate <= 0)
        {
            Warn(logger, "NETHERMIND_MDBX_PROFILE_HOTPATH_SAMPLE_RATE must be positive. Using the default value.");
            hotPathSampleRate = DefaultHotPathSampleRate;
        }

        profileIntervalSeconds = Math.Max(1, profileIntervalSeconds);
        slowTransactionMilliseconds = Math.Max(0, slowTransactionMilliseconds);

        return new MdbxTuningOptions(
            initialMapSize,
            maxMapSize,
            growthStep,
            shrinkThreshold,
            pageSize,
            maxDbs,
            maxReaders,
            rpAugmentLimit,
            dirtyPagesReserveLimit,
            transactionDirtyPagesLimit,
            transactionDirtyPagesInitial,
            syncBytes,
            syncPeriodSeconds,
            enableReadAhead,
            enableWriteMap,
            enableCoalesce,
            enableBatchGrouping,
            enableAppend,
            disableWalSyncMode,
            maxBatchGroupOperations,
            maxBatchGroupBytes,
            enableProfiling,
            hotPathSampleRate,
            TimeSpan.FromSeconds(profileIntervalSeconds),
            TimeSpan.FromMilliseconds(slowTransactionMilliseconds),
            hasDirtyPagesReserveLimitOverride,
            hasTransactionDirtyPagesLimitOverride,
            hasTransactionDirtyPagesInitialOverride,
            hasOverrides);
    }

    public MdbxTuningOptions WithActualPageSize(int actualPageSize, bool isStateDb)
    {
        if (!isStateDb)
        {
            return this with { PageSize = actualPageSize };
        }

        return this with
        {
            PageSize = actualPageSize,
            DirtyPagesReserveLimit = HasDirtyPagesReserveLimitOverride
                ? DirtyPagesReserveLimit
                : BytesToPages(DefaultStateDirtyPagesReserveBytes, actualPageSize),
            TransactionDirtyPagesLimit = HasTransactionDirtyPagesLimitOverride
                ? TransactionDirtyPagesLimit
                : BytesToPages(DefaultStateTransactionDirtyPagesLimitBytes, actualPageSize),
            TransactionDirtyPagesInitial = HasTransactionDirtyPagesInitialOverride
                ? TransactionDirtyPagesInitial
                : BytesToPages(DefaultStateTransactionDirtyPagesInitialBytes, actualPageSize),
        };
    }

    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"initialMap={FormatBytes(InitialMapSize)} maxMap={FormatBytes(MaxMapSize)} growthStep={FormatBytes(GrowthStep)} shrinkThreshold={FormatBytes(ShrinkThreshold)} pageSize={PageSize} maxDbs={MaxDbs} maxReaders={MaxReaders} rpAugmentLimit={RpAugmentLimit} dirtyPagesReserveLimit={DirtyPagesReserveLimit} txnDirtyPagesLimit={TransactionDirtyPagesLimit} txnDirtyPagesInitial={TransactionDirtyPagesInitial} syncBytes={FormatBytes(SyncBytes)} syncPeriod={SyncPeriodSeconds}s readAhead={EnableReadAhead} writeMap={EnableWriteMap} coalesce={EnableCoalesce} batchGroup={EnableBatchGrouping} append={EnableAppend} disableWalSync={DisableWalSyncMode} batchGroupMaxOps={MaxBatchGroupOperations} batchGroupMaxBytes={FormatBytes(MaxBatchGroupBytes)} profile={EnableProfiling} profileHotPathSampleRate={HotPathSampleRate} profileInterval={ProfileInterval.TotalSeconds:F0}s slowTransaction={SlowTransactionThreshold.TotalMilliseconds:F0}ms");

    internal static bool TryParseSize(string value, out long result)
    {
        result = 0;
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        string number = trimmed;
        decimal multiplier = 1;
        if (TryRemoveSuffix(trimmed, "KiB", out number))
        {
            multiplier = 1L << 10;
        }
        else if (TryRemoveSuffix(trimmed, "MiB", out number))
        {
            multiplier = 1L << 20;
        }
        else if (TryRemoveSuffix(trimmed, "GiB", out number))
        {
            multiplier = 1L << 30;
        }
        else if (TryRemoveSuffix(trimmed, "TiB", out number))
        {
            multiplier = 1L << 40;
        }
        else if (TryRemoveSuffix(trimmed, "KB", out number))
        {
            multiplier = 1_000;
        }
        else if (TryRemoveSuffix(trimmed, "MB", out number))
        {
            multiplier = 1_000_000;
        }
        else if (TryRemoveSuffix(trimmed, "GB", out number))
        {
            multiplier = 1_000_000_000;
        }
        else if (TryRemoveSuffix(trimmed, "TB", out number))
        {
            multiplier = 1_000_000_000_000;
        }
        else if (TryRemoveSuffix(trimmed, "B", out number))
        {
            multiplier = 1;
        }

        if (!decimal.TryParse(number.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed) || parsed < 0)
        {
            return false;
        }

        decimal bytes = decimal.Truncate(parsed * multiplier);
        if (bytes > long.MaxValue)
        {
            return false;
        }

        result = (long)bytes;
        return true;
    }

    internal static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    internal static bool TryParseDisableWalSyncMode(string value, out MdbxDisableWalSyncMode result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
            case "off":
            case "false":
            case "0":
                result = MdbxDisableWalSyncMode.None;
                return true;
            case "nometasync":
            case "no-meta-sync":
            case "meta":
                result = MdbxDisableWalSyncMode.NoMetaSync;
                return true;
            case "safenosync":
            case "safe-nosync":
            case "nosync":
            case "no-sync":
            case "true":
            case "1":
                result = MdbxDisableWalSyncMode.SafeNoSync;
                return true;
            default:
                result = MdbxDisableWalSyncMode.None;
                return false;
        }
    }

    private static bool TryRemoveSuffix(string value, string suffix, out string number)
    {
        if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^suffix.Length];
            return true;
        }

        number = value;
        return false;
    }

    private static long ReadSize(string name, long fallback, ILogger logger, ref bool hasOverrides)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        hasOverrides = true;
        if (TryParseSize(value, out long parsed))
        {
            return parsed;
        }

        Warn(logger, $"{name}='{value}' is not a valid size. Using {FormatBytes(fallback)}.");
        return fallback;
    }

    private static ulong ReadUInt64(string name, ulong fallback, ILogger logger, ref bool hasOverrides) =>
        ReadUInt64(name, fallback, logger, ref hasOverrides, out _);

    private static ulong ReadUInt64(string name, ulong fallback, ILogger logger, ref bool hasOverrides, out bool hasOverride)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            hasOverride = false;
            return fallback;
        }

        hasOverrides = true;
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
        {
            hasOverride = true;
            return parsed;
        }

        hasOverride = false;
        Warn(logger, $"{name}='{value}' is not a valid unsigned integer. Using {fallback}.");
        return fallback;
    }

    private static int ReadInt32(string name, int fallback, ILogger logger, ref bool hasOverrides)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        hasOverrides = true;
        if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        Warn(logger, $"{name}='{value}' is not a valid integer. Using {fallback}.");
        return fallback;
    }

    private static bool ReadBool(string name, bool fallback, ILogger logger, ref bool hasOverrides)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        hasOverrides = true;
        if (TryParseBool(value, out bool parsed))
        {
            return parsed;
        }

        Warn(logger, $"{name}='{value}' is not a valid boolean. Using {fallback}.");
        return fallback;
    }

    private static MdbxDisableWalSyncMode ReadDisableWalSyncMode(string name, MdbxDisableWalSyncMode fallback, ILogger logger, ref bool hasOverrides)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        hasOverrides = true;
        if (TryParseDisableWalSyncMode(value, out MdbxDisableWalSyncMode parsed))
        {
            return parsed;
        }

        Warn(logger, $"{name}='{value}' is not a valid MDBX DisableWAL sync mode. Use none, nometasync, or safe-nosync.");
        return fallback;
    }

    private static void Warn(ILogger logger, string message)
    {
        if (logger.IsWarn)
        {
            logger.Warn(message);
        }
    }

    internal static ulong BytesToPages(long bytes, int pageSize) =>
        (ulong)Math.Max(1, bytes / pageSize);

    internal static bool IsValidPageSize(int pageSize) =>
        pageSize >= 4096 && pageSize <= 65536 && BitOperations.IsPow2(pageSize);

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1L << 40 when bytes % (1L << 40) == 0 => $"{bytes / (1L << 40)}TiB",
            >= 1L << 30 when bytes % (1L << 30) == 0 => $"{bytes / (1L << 30)}GiB",
            >= 1L << 20 when bytes % (1L << 20) == 0 => $"{bytes / (1L << 20)}MiB",
            >= 1L << 10 when bytes % (1L << 10) == 0 => $"{bytes / (1L << 10)}KiB",
            _ => bytes.ToString(CultureInfo.InvariantCulture),
        };
}

internal enum MdbxDisableWalSyncMode
{
    None,
    NoMetaSync,
    SafeNoSync,
}
