// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ProgressLoggerTests
{
    [Test]
    public void Current_per_second_uninitialized()
    {
        ProgressLogger progressLogger = CreateProgress();
        Assert.That(progressLogger.CurrentPerSecond, Is.EqualTo(decimal.Zero));
    }

    [Test]
    public void Total_per_second_uninitialized()
    {
        ProgressLogger progressLogger = CreateProgress();
        Assert.That(progressLogger.TotalPerSecond, Is.EqualTo(decimal.Zero));
    }

    [Test]
    public void Current_value_uninitialized()
    {
        ProgressLogger progressLogger = CreateProgress();
        Assert.That(progressLogger.CurrentValue, Is.EqualTo(0L));
    }

    [Test]
    public void Update_0L()
    {
        ProgressLogger progressLogger = CreateProgress();
        progressLogger.Update(0L);
        Assert.That(progressLogger.CurrentValue, Is.EqualTo(0L));
    }

    [Test]
    public void Update_0L_total_per_second()
    {
        ProgressLogger progressLogger = CreateProgress();
        progressLogger.Update(0L);
        Assert.That(progressLogger.TotalPerSecond, Is.EqualTo(0L));
    }

    [Test]
    public void Update_0L_current_per_second()
    {
        ProgressLogger progressLogger = CreateProgress();
        progressLogger.Update(0L);
        Assert.That(progressLogger.CurrentPerSecond, Is.EqualTo(0L));
    }

    [Test]
    [Retry(3)]
    public void Update_twice_total_per_second()
    {
        (ProgressLogger measuredProgress, ManualTimestamper manualTimestamper) = CreateProgressWithManualTimestamper();
        measuredProgress.Update(0L);
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(1L);
        Assert.That(measuredProgress.TotalPerSecond, Is.GreaterThanOrEqualTo(4M));
        Assert.That(measuredProgress.TotalPerSecond, Is.LessThanOrEqualTo(10M));
    }

    [Test]
    [Retry(3)]
    public void Update_twice_current_per_second()
    {
        (ProgressLogger measuredProgress, ManualTimestamper manualTimestamper) = CreateProgressWithManualTimestamper();
        measuredProgress.Update(0L);
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(1L);
        Assert.That(measuredProgress.CurrentPerSecond, Is.LessThanOrEqualTo(10M));
        Assert.That(measuredProgress.CurrentPerSecond, Is.GreaterThanOrEqualTo(4M));
    }

    [Test]
    public void Current_starting_from_non_zero()
    {
        (ProgressLogger measuredProgress, ManualTimestamper manualTimestamper) = CreateProgressWithManualTimestamper();
        measuredProgress.Update(10L);
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(20L);
        Assert.That(measuredProgress.CurrentPerSecond, Is.LessThanOrEqualTo(105M));
    }

    [Test]
    public void Update_thrice_result_per_second()
    {
        (ProgressLogger measuredProgress, ManualTimestamper manualTimestamper) = CreateProgressWithManualTimestamper();
        measuredProgress.Update(0L);
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(1L);
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(3L);
        Assert.That(measuredProgress.TotalPerSecond, Is.GreaterThanOrEqualTo(6M));
        Assert.That(measuredProgress.TotalPerSecond, Is.LessThanOrEqualTo(15M));
        Assert.That(measuredProgress.CurrentPerSecond, Is.GreaterThanOrEqualTo(6M));
        Assert.That(measuredProgress.CurrentPerSecond, Is.LessThanOrEqualTo(30M));
    }

    [Test]
    public void After_ending_does_not_update_total_or_current()
    {
        (ProgressLogger measuredProgress, ManualTimestamper manualTimestamper) = CreateProgressWithManualTimestamper();
        measuredProgress.Update(0L);
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(1L);
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(3L);
        measuredProgress.MarkEnd();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.SetMeasuringPoint();
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.SetMeasuringPoint();
        Assert.That(measuredProgress.TotalPerSecond, Is.GreaterThanOrEqualTo(6M));
        Assert.That(measuredProgress.TotalPerSecond, Is.LessThanOrEqualTo(15M));
        Assert.That(measuredProgress.CurrentPerSecond, Is.EqualTo(0M));
    }

    [Test]
    public void Has_ended_returns_true_when_ended()
    {
        ProgressLogger progressLogger = CreateProgress();
        progressLogger.MarkEnd();
        Assert.That(progressLogger.HasEnded, Is.True);
    }

    [Test]
    public void Has_ended_returns_false_when_ended()
    {
        ProgressLogger progressLogger = CreateProgress();
        Assert.That(progressLogger.HasEnded, Is.False);
    }

    [Test]
    public void Print_Progress()
    {
        ManualTimestamper manualTimestamper = new();
        ILogManager logManager = Substitute.For<ILogManager>();
        InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
        iLogger.IsInfo.Returns(true);
        ILogger logger = new(iLogger);
        logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

        ProgressLogger measuredProgress = new("Progress", logManager, manualTimestamper);

        measuredProgress.Reset(0L, 100);
        measuredProgress.SetMeasuringPoint();
        measuredProgress.IncrementSkipped(10);
        measuredProgress.CurrentQueued = 99;
        manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
        measuredProgress.Update(1L);

        measuredProgress.LogProgress();

        iLogger.Received(1).Info("Progress              1 /        100 (  1.00 %) [⡆                                    ] queue       99 | skipped      90 Blk/s | current      10 Blk/s");
    }

    private ProgressLogger CreateProgress()
    {
        return new("", LimboLogs.Instance);
    }

    private (ProgressLogger, ManualTimestamper) CreateProgressWithManualTimestamper()
    {
        ManualTimestamper manualTimestamper = new();
        ProgressLogger progressLogger = new("", LimboLogs.Instance, manualTimestamper);
        return (progressLogger, manualTimestamper);
    }

}
