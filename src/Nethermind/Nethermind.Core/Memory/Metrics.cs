// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;

namespace Nethermind.Core.Memory;

/// <summary>Metrics for asynchronous no-GC-region admission and cleanup.</summary>
public static class Metrics
{
    private static long _eligible;
    private static long _ineligible;
    private static long _queued;
    private static long _shared;
    private static long _skipped;
    private static long _admitted;
    private static long _refused;
    private static long _admissionExceptions;
    private static long _queueFailures;
    private static long _abandonedBeforeAdmission;
    private static long _lateAdmissions;
    private static long _brokenAtEnd;
    private static long _cleanupExceptions;
    private static long _queueWaitSamples;
    private static long _queueWaitStopwatchTicks;
    private static long _runtimeAttemptSamples;
    private static long _runtimeAttemptStopwatchTicks;

    [CounterMetric]
    [Description("No-GC-region attempts allowed by the GC strategy")]
    public static long GcRegionEligibleTotal => Interlocked.Read(ref _eligible);

    [CounterMetric]
    [Description("No-GC-region attempts disallowed by the GC strategy")]
    public static long GcRegionIneligibleTotal => Interlocked.Read(ref _ineligible);

    [CounterMetric]
    [Description("No-GC-region work items accepted by the configured queue")]
    public static long GcRegionQueuedTotal => Interlocked.Read(ref _queued);

    [CounterMetric]
    [Description("Payloads that took a lease on an existing pending or active no-GC-region attempt")]
    public static long GcRegionSharedTotal => Interlocked.Read(ref _shared);

    [CounterMetric]
    [Description("Eligible no-GC-region attempts skipped because the keeper was disposed or a region could not be shared")]
    public static long GcRegionSkippedTotal => Interlocked.Read(ref _skipped);

    [CounterMetric]
    [Description("Successful runtime no-GC-region admissions; this does not imply full-block protection")]
    public static long GcRegionAdmittedTotal => Interlocked.Read(ref _admitted);

    [CounterMetric]
    [Description("Runtime no-GC-region admission attempts declined by the runtime")]
    public static long GcRegionRefusedTotal => Interlocked.Read(ref _refused);

    [CounterMetric]
    [Description("Runtime no-GC-region admission attempts that threw")]
    public static long GcRegionAdmissionExceptionsTotal => Interlocked.Read(ref _admissionExceptions);

    [CounterMetric]
    [Description("No-GC-region work item submissions rejected by the configured queue")]
    public static long GcRegionQueueFailuresTotal => Interlocked.Read(ref _queueFailures);

    [CounterMetric]
    [Description("Queued no-GC-region work items released before runtime admission started")]
    public static long GcRegionAbandonedBeforeAdmissionTotal => Interlocked.Read(ref _abandonedBeforeAdmission);

    [CounterMetric]
    [Description("Successful runtime admissions observed after their last lease was released")]
    public static long GcRegionLateAdmissionsTotal => Interlocked.Read(ref _lateAdmissions);

    [CounterMetric]
    [Description("Successful runtime admissions found inactive when cleanup checks the region, including late admissions; cleanup exceptions are separate")]
    public static long GcRegionBrokenAtEndTotal => Interlocked.Read(ref _brokenAtEnd);

    [CounterMetric]
    [Description("Exceptions while checking or ending an admitted no-GC region")]
    public static long GcRegionCleanupExceptionsTotal => Interlocked.Read(ref _cleanupExceptions);

    [ExponentialPowerHistogramMetric(Start = 0.000001, Factor = 2, Count = 24)]
    [Description("Time from queue submission until the work item starts, in seconds")]
    public static IMetricObserver GcRegionQueueWaitSeconds { get; set; } = NoopMetricObserver.Instance;

    [ExponentialPowerHistogramMetric(Start = 0.000001, Factor = 2, Count = 24)]
    [Description("Runtime no-GC-region admission attempt duration, in seconds")]
    public static IMetricObserver GcRegionRuntimeAdmissionSeconds { get; set; } = NoopMetricObserver.Instance;

    internal static void RecordEligibility(bool eligible)
    {
        if (eligible) Interlocked.Increment(ref _eligible);
        else Interlocked.Increment(ref _ineligible);
    }

    internal static void RecordQueued() => Interlocked.Increment(ref _queued);

    internal static void RecordShared() => Interlocked.Increment(ref _shared);

    internal static void RecordSkipped() => Interlocked.Increment(ref _skipped);

    internal static void RecordAdmitted() => Interlocked.Increment(ref _admitted);

    internal static void RecordRefused() => Interlocked.Increment(ref _refused);

    internal static void RecordAdmissionException() => Interlocked.Increment(ref _admissionExceptions);

    internal static void RecordQueueFailure() => Interlocked.Increment(ref _queueFailures);

    internal static void RecordAbandonedBeforeAdmission() => Interlocked.Increment(ref _abandonedBeforeAdmission);

    internal static void RecordLateAdmission() => Interlocked.Increment(ref _lateAdmissions);

    internal static void RecordBrokenAtEnd() => Interlocked.Increment(ref _brokenAtEnd);

    internal static void RecordCleanupException() => Interlocked.Increment(ref _cleanupExceptions);

    internal static void RecordQueueWait(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _queueWaitSamples);
        Interlocked.Add(ref _queueWaitStopwatchTicks, elapsedStopwatchTicks);
        GcRegionQueueWaitSeconds.Observe((double)elapsedStopwatchTicks / Stopwatch.Frequency);
    }

    internal static void RecordRuntimeAdmissionDuration(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _runtimeAttemptSamples);
        Interlocked.Add(ref _runtimeAttemptStopwatchTicks, elapsedStopwatchTicks);
        GcRegionRuntimeAdmissionSeconds.Observe((double)elapsedStopwatchTicks / Stopwatch.Frequency);
    }

    internal static string CreateShutdownSummary()
    {
        long queueWaitSamples = Interlocked.Read(ref _queueWaitSamples);
        long runtimeAttemptSamples = Interlocked.Read(ref _runtimeAttemptSamples);
        double averageQueueWaitMs = AverageMilliseconds(Interlocked.Read(ref _queueWaitStopwatchTicks), queueWaitSamples);
        double averageRuntimeAdmissionMs = AverageMilliseconds(Interlocked.Read(ref _runtimeAttemptStopwatchTicks), runtimeAttemptSamples);
        return string.Create(CultureInfo.InvariantCulture,
            $"GCKeeper lifecycle process totals at dispose (in-flight entry work may finish after this snapshot): eligible={GcRegionEligibleTotal}, ineligible={GcRegionIneligibleTotal}, queued={GcRegionQueuedTotal}, shared={GcRegionSharedTotal}, skipped={GcRegionSkippedTotal}, admitted={GcRegionAdmittedTotal}, refused={GcRegionRefusedTotal}, admissionFaults={GcRegionAdmissionExceptionsTotal}, queueFailures={GcRegionQueueFailuresTotal}, abandonedBeforeAdmission={GcRegionAbandonedBeforeAdmissionTotal}, lateAdmissions={GcRegionLateAdmissionsTotal}, brokenAtEnd={GcRegionBrokenAtEndTotal}, cleanupFaults={GcRegionCleanupExceptionsTotal}, queueWaitSamples={queueWaitSamples}, averageQueueWaitMs={averageQueueWaitMs:F3}, runtimeAttempts={runtimeAttemptSamples}, averageRuntimeAdmissionMs={averageRuntimeAdmissionMs:F3}");
    }

    private static double AverageMilliseconds(long elapsedStopwatchTicks, long samples) =>
        samples == 0 ? 0 : (double)elapsedStopwatchTicks * 1_000 / Stopwatch.Frequency / samples;
}
