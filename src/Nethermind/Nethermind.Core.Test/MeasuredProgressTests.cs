// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class MeasuredProgressTests
    {
        [Test]
        public void Current_per_second_uninitialized()
        {
            MeasuredProgress measuredProgress = new();
            Assert.That(measuredProgress.CurrentPerSecond, Is.EqualTo(decimal.Zero));
        }

        [Test]
        public void Total_per_second_uninitialized()
        {
            MeasuredProgress measuredProgress = new();
            Assert.That(measuredProgress.TotalPerSecond, Is.EqualTo(decimal.Zero));
        }

        [Test]
        public void Current_value_uninitialized()
        {
            MeasuredProgress measuredProgress = new();
            Assert.That(measuredProgress.CurrentValue, Is.EqualTo(0L));
        }

        [Test]
        public void Update_0L()
        {
            MeasuredProgress measuredProgress = new();
            measuredProgress.Update(0L);
            Assert.That(measuredProgress.CurrentValue, Is.EqualTo(0L));
        }

        [Test]
        public void Update_0L_total_per_second()
        {
            MeasuredProgress measuredProgress = new();
            measuredProgress.Update(0L);
            Assert.That(measuredProgress.TotalPerSecond, Is.EqualTo(0L));
        }

        [Test]
        public void Update_0L_current_per_second()
        {
            MeasuredProgress measuredProgress = new();
            measuredProgress.Update(0L);
            Assert.That(measuredProgress.CurrentPerSecond, Is.EqualTo(0L));
        }

        [Test]
        [Retry(3)]
        public void Update_twice_total_per_second()
        {
            ManualTimestamper manualTimestamper = new();
            MeasuredProgress measuredProgress = new(manualTimestamper);
            measuredProgress.Update(0L);
            measuredProgress.SetMeasuringPoint();
            manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
            measuredProgress.Update(1L);
            Assert.GreaterOrEqual(measuredProgress.TotalPerSecond, 4M);
            Assert.LessOrEqual(measuredProgress.TotalPerSecond, 10M);
        }

        [Test]
        [Retry(3)]
        public void Update_twice_current_per_second()
        {
            ManualTimestamper manualTimestamper = new();
            MeasuredProgress measuredProgress = new(manualTimestamper);
            measuredProgress.Update(0L);
            measuredProgress.SetMeasuringPoint();
            manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
            measuredProgress.Update(1L);
            Assert.LessOrEqual(measuredProgress.CurrentPerSecond, 10M);
            Assert.GreaterOrEqual(measuredProgress.CurrentPerSecond, 4M);
        }

        [Test]
        public void Current_starting_from_non_zero()
        {
            ManualTimestamper manualTimestamper = new();
            MeasuredProgress measuredProgress = new(manualTimestamper);
            measuredProgress.Update(10L);
            measuredProgress.SetMeasuringPoint();
            manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
            measuredProgress.Update(20L);
            Assert.LessOrEqual(measuredProgress.CurrentPerSecond, 105M);
        }

        [Test]
        public void Update_thrice_result_per_second()
        {
            ManualTimestamper manualTimestamper = new();
            MeasuredProgress measuredProgress = new(manualTimestamper);
            measuredProgress.Update(0L);
            measuredProgress.SetMeasuringPoint();
            manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
            measuredProgress.Update(1L);
            measuredProgress.SetMeasuringPoint();
            manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
            measuredProgress.Update(3L);
            Assert.GreaterOrEqual(measuredProgress.TotalPerSecond, 6M);
            Assert.LessOrEqual(measuredProgress.TotalPerSecond, 15M);
            Assert.GreaterOrEqual(measuredProgress.CurrentPerSecond, 6M);
            Assert.LessOrEqual(measuredProgress.CurrentPerSecond, 30M);
        }

        [Test]
        public void After_ending_does_not_update_total_or_current()
        {
            ManualTimestamper manualTimestamper = new();
            MeasuredProgress measuredProgress = new(manualTimestamper);
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
            Assert.GreaterOrEqual(measuredProgress.TotalPerSecond, 6M);
            Assert.LessOrEqual(measuredProgress.TotalPerSecond, 15M);
            Assert.That(measuredProgress.CurrentPerSecond, Is.EqualTo(0M));
        }

        [Test]
        public void Has_ended_returns_true_when_ended()
        {
            MeasuredProgress measuredProgress = new();
            measuredProgress.MarkEnd();
            Assert.True(measuredProgress.HasEnded);
        }

        [Test]
        public void Has_ended_returns_false_when_ended()
        {
            MeasuredProgress measuredProgress = new();
            Assert.False(measuredProgress.HasEnded);
        }
    }
}
