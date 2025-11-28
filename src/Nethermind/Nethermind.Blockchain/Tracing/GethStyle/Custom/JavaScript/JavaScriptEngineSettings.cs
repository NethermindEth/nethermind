// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

internal sealed record class JavaScriptEngineSettings
{
    private const string Prefix = "NETHERMIND_JS_TRACER_";
    private const string MaxNewSpaceEnv = Prefix + "MAX_NEW_SPACE_MB";
    private const string MaxOldSpaceEnv = Prefix + "MAX_OLD_SPACE_MB";
    private const string MaxArrayBufferEnv = Prefix + "MAX_ARRAY_BUFFER_MB";
    private const string HeapMultiplierEnv = Prefix + "HEAP_EXPANSION_MULTIPLIER";
    private const string MaxHeapEnv = Prefix + "MAX_HEAP_MB";
    private const string MaxStackEnv = Prefix + "MAX_STACK_MB";
    private const string GcThresholdEnv = Prefix + "GC_THRESHOLD_MB";
    private const string GcIntervalEnv = Prefix + "MIN_GC_INTERVAL_MS";
    private const string ExhaustiveGcEnv = Prefix + "EXHAUSTIVE_GC";
    private const string HeapSampleIntervalEnv = Prefix + "HEAP_SAMPLE_INTERVAL_MS";

    private const long BytesPerMiB = 1024L * 1024L;

    public static JavaScriptEngineSettings Default { get; } = new();

    public int MaxNewSpaceSizeMiB { get; init; } = 64;

    public int MaxOldSpaceSizeMiB { get; init; } = 1024;

    public int MaxArrayBufferAllocationMiB { get; init; } = 512;

    public double HeapExpansionMultiplier { get; init; } = 1.5d;

    public long MaxHeapSizeBytes { get; init; } = 768L * BytesPerMiB;

    public long MaxStackUsageBytes { get; init; } = 32L * BytesPerMiB;

    public long GarbageCollectionThresholdBytes { get; init; } = 512L * BytesPerMiB;

    public TimeSpan MinGarbageCollectionInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public bool ExhaustiveGarbageCollection { get; init; }

    public TimeSpan HeapSizeSampleInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    public static JavaScriptEngineSettings FromEnvironment()
    {
        JavaScriptEngineSettings defaults = Default;

        return defaults with
        {
            MaxNewSpaceSizeMiB = ReadInt(MaxNewSpaceEnv, defaults.MaxNewSpaceSizeMiB),
            MaxOldSpaceSizeMiB = ReadInt(MaxOldSpaceEnv, defaults.MaxOldSpaceSizeMiB),
            MaxArrayBufferAllocationMiB = ReadInt(MaxArrayBufferEnv, defaults.MaxArrayBufferAllocationMiB),
            HeapExpansionMultiplier = ReadDouble(HeapMultiplierEnv, defaults.HeapExpansionMultiplier),
            MaxHeapSizeBytes = ReadBytes(MaxHeapEnv, defaults.MaxHeapSizeBytes),
            MaxStackUsageBytes = ReadBytes(MaxStackEnv, defaults.MaxStackUsageBytes),
            GarbageCollectionThresholdBytes = ReadBytes(GcThresholdEnv, defaults.GarbageCollectionThresholdBytes),
            MinGarbageCollectionInterval = ReadTimeSpan(GcIntervalEnv, defaults.MinGarbageCollectionInterval),
            ExhaustiveGarbageCollection = ReadBool(ExhaustiveGcEnv, defaults.ExhaustiveGarbageCollection),
            HeapSizeSampleInterval = ReadTimeSpan(HeapSampleIntervalEnv, defaults.HeapSizeSampleInterval)
        };
    }

    private static int ReadInt(string envName, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(envName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static double ReadDouble(string envName, double fallback)
    {
        string? value = Environment.GetEnvironmentVariable(envName);
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static long ReadBytes(string envName, long fallback)
    {
        string? value = Environment.GetEnvironmentVariable(envName);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
            ? parsed * BytesPerMiB
            : fallback;
    }

    private static TimeSpan ReadTimeSpan(string envName, TimeSpan fallback)
    {
        string? value = Environment.GetEnvironmentVariable(envName);
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0
            ? TimeSpan.FromMilliseconds(parsed)
            : fallback;
    }

    private static bool ReadBool(string envName, bool fallback)
    {
        string? value = Environment.GetEnvironmentVariable(envName);
        return bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }
}

internal static class JavaScriptEngineSettingsProvider
{
    public static JavaScriptEngineSettings Current { get; } = JavaScriptEngineSettings.FromEnvironment();
}
