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
    bool EnableReadAhead,
    bool EnableWriteMap,
    bool EnableCoalesce,
    bool EnableBatchGrouping,
    int MaxBatchGroupOperations,
    long MaxBatchGroupBytes,
    bool EnableProfiling,
    int HotPathSampleRate,
    TimeSpan ProfileInterval,
    TimeSpan SlowTransactionThreshold,
    bool HasOverrides)
{
    public const long DefaultInitialMapSize = 64L << 20;
    public const long DefaultMaxMapSize = 1L << 40;
    public const long DefaultGrowthStep = 1L << 30;
    public const long DefaultShrinkThreshold = 1L << 30;
    public const int DefaultPageSize = 16 * 1024;
    public const ulong DefaultMaxDbs = 512;
    public const ulong DefaultMaxReaders = 8192;
    public const ulong DefaultRpAugmentLimit = 256 * 1024;
    public const int DefaultMaxBatchGroupOperations = 128 * 1024;
    public const long DefaultMaxBatchGroupBytes = 64L << 20;

    private const int DefaultProfileIntervalSeconds = 30;
    private const int DefaultSlowTransactionMilliseconds = 1_000;
    private const int DefaultHotPathSampleRate = 128;

    public static MdbxTuningOptions ReadFromEnvironment(ILogger logger)
    {
        bool hasOverrides = false;
        long initialMapSize = ReadSize("NETHERMIND_MDBX_INITIAL_MAP_SIZE", DefaultInitialMapSize, logger, ref hasOverrides);
        long maxMapSize = ReadSize("NETHERMIND_MDBX_MAX_MAP_SIZE", DefaultMaxMapSize, logger, ref hasOverrides);
        long growthStep = ReadSize("NETHERMIND_MDBX_GROWTH_STEP", DefaultGrowthStep, logger, ref hasOverrides);
        long shrinkThreshold = ReadSize("NETHERMIND_MDBX_SHRINK_THRESHOLD", DefaultShrinkThreshold, logger, ref hasOverrides);
        int pageSize = ReadInt32("NETHERMIND_MDBX_PAGE_SIZE", DefaultPageSize, logger, ref hasOverrides);
        ulong maxDbs = ReadUInt64("NETHERMIND_MDBX_MAX_DBS", DefaultMaxDbs, logger, ref hasOverrides);
        ulong maxReaders = ReadUInt64("NETHERMIND_MDBX_MAX_READERS", DefaultMaxReaders, logger, ref hasOverrides);
        ulong rpAugmentLimit = ReadUInt64("NETHERMIND_MDBX_RP_AUGMENT_LIMIT", DefaultRpAugmentLimit, logger, ref hasOverrides);
        ulong dirtyPagesReserveLimit = ReadUInt64("NETHERMIND_MDBX_DIRTY_PAGES_RESERVE_LIMIT", 0, logger, ref hasOverrides);
        ulong transactionDirtyPagesLimit = ReadUInt64("NETHERMIND_MDBX_TXN_DIRTY_PAGES_LIMIT", 0, logger, ref hasOverrides);
        ulong transactionDirtyPagesInitial = ReadUInt64("NETHERMIND_MDBX_TXN_DIRTY_PAGES_INITIAL", 0, logger, ref hasOverrides);
        bool enableReadAhead = ReadBool("NETHERMIND_MDBX_READAHEAD", fallback: false, logger, ref hasOverrides);
        bool enableWriteMap = ReadBool("NETHERMIND_MDBX_WRITEMAP", fallback: true, logger, ref hasOverrides);
        bool enableCoalesce = ReadBool("NETHERMIND_MDBX_COALESCE", fallback: true, logger, ref hasOverrides);
        bool enableBatchGrouping = ReadBool("NETHERMIND_MDBX_BATCH_GROUP", fallback: true, logger, ref hasOverrides);
        int maxBatchGroupOperations = ReadInt32("NETHERMIND_MDBX_BATCH_GROUP_MAX_OPS", DefaultMaxBatchGroupOperations, logger, ref hasOverrides);
        long maxBatchGroupBytes = ReadSize("NETHERMIND_MDBX_BATCH_GROUP_MAX_BYTES", DefaultMaxBatchGroupBytes, logger, ref hasOverrides);
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

        if (pageSize < 4096 || pageSize > 65536 || !BitOperations.IsPow2(pageSize))
        {
            Warn(logger, "NETHERMIND_MDBX_PAGE_SIZE must be a power of two between 4096 and 65536. Using the default value.");
            pageSize = DefaultPageSize;
        }

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
            maxBatchGroupOperations = DefaultMaxBatchGroupOperations;
        }

        if (maxBatchGroupBytes <= 0)
        {
            Warn(logger, "NETHERMIND_MDBX_BATCH_GROUP_MAX_BYTES must be positive. Using the default value.");
            maxBatchGroupBytes = DefaultMaxBatchGroupBytes;
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
            enableReadAhead,
            enableWriteMap,
            enableCoalesce,
            enableBatchGrouping,
            maxBatchGroupOperations,
            maxBatchGroupBytes,
            enableProfiling,
            hotPathSampleRate,
            TimeSpan.FromSeconds(profileIntervalSeconds),
            TimeSpan.FromMilliseconds(slowTransactionMilliseconds),
            hasOverrides);
    }

    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"initialMap={FormatBytes(InitialMapSize)} maxMap={FormatBytes(MaxMapSize)} growthStep={FormatBytes(GrowthStep)} shrinkThreshold={FormatBytes(ShrinkThreshold)} pageSize={PageSize} maxDbs={MaxDbs} maxReaders={MaxReaders} rpAugmentLimit={RpAugmentLimit} dirtyPagesReserveLimit={DirtyPagesReserveLimit} txnDirtyPagesLimit={TransactionDirtyPagesLimit} txnDirtyPagesInitial={TransactionDirtyPagesInitial} readAhead={EnableReadAhead} writeMap={EnableWriteMap} coalesce={EnableCoalesce} batchGroup={EnableBatchGrouping} batchGroupMaxOps={MaxBatchGroupOperations} batchGroupMaxBytes={FormatBytes(MaxBatchGroupBytes)} profile={EnableProfiling} profileHotPathSampleRate={HotPathSampleRate} profileInterval={ProfileInterval.TotalSeconds:F0}s slowTransaction={SlowTransactionThreshold.TotalMilliseconds:F0}ms");

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

    private static ulong ReadUInt64(string name, ulong fallback, ILogger logger, ref bool hasOverrides)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        hasOverrides = true;
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
        {
            return parsed;
        }

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

    private static void Warn(ILogger logger, string message)
    {
        if (logger.IsWarn)
        {
            logger.Warn(message);
        }
    }

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
