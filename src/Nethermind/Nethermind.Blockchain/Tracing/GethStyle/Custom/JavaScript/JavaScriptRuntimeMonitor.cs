// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Microsoft.ClearScript.V8;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

internal static class JavaScriptRuntimeMonitor
{
    private static readonly object GcSync = new();
    private static long _lastGcTimestamp;
    private static long _lastObservedUsage;

    public static void OnEngineDisposed(V8Runtime runtime, JavaScriptEngineSettings settings)
    {
        MaybeCollectGarbage(settings, () => CaptureSnapshot(runtime), runtime.CollectGarbage);
    }

    internal static void MaybeCollectGarbage(
        JavaScriptEngineSettings settings,
        Func<JavaScriptRuntimeHeapSnapshot> snapshotProvider,
        Action<bool> collectGarbage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(collectGarbage);

        if (settings.GarbageCollectionThresholdBytes <= 0)
        {
            return;
        }

        JavaScriptRuntimeHeapSnapshot snapshot = snapshotProvider();
        if (snapshot.UsedBytes < settings.GarbageCollectionThresholdBytes)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long minTicks = GetMinIntervalTicks(settings.MinGarbageCollectionInterval);

        lock (GcSync)
        {
            if (now - _lastGcTimestamp < minTicks &&
                snapshot.UsedBytes <= _lastObservedUsage + settings.GarbageCollectionThresholdBytes / 8)
            {
                return;
            }

            collectGarbage(settings.ExhaustiveGarbageCollection);
            _lastGcTimestamp = now;
            _lastObservedUsage = snapshotProvider().UsedBytes;
        }
    }

    internal static void ResetForTests()
    {
        lock (GcSync)
        {
            _lastGcTimestamp = 0;
            _lastObservedUsage = 0;
        }
    }

    internal readonly record struct JavaScriptRuntimeHeapSnapshot(long UsedBytes, long HeapLimitBytes);

    private static JavaScriptRuntimeHeapSnapshot CaptureSnapshot(V8Runtime runtime)
    {
        V8RuntimeHeapInfo info = runtime.GetHeapInfo();
        return new JavaScriptRuntimeHeapSnapshot(
            ClampToInt64(info.UsedHeapSize),
            ClampToInt64(info.HeapSizeLimit));
    }

    private static long ClampToInt64(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static long GetMinIntervalTicks(TimeSpan interval) =>
        interval <= TimeSpan.Zero ? 0 : (long)(interval.TotalSeconds * Stopwatch.Frequency);
}
