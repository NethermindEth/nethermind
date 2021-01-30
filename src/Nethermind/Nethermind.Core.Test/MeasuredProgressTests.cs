//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class MeasuredProgressTests
    {
        [Test]
        public void Current_per_second_uninitialized()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            Assert.AreEqual(decimal.Zero, measuredProgress.CurrentPerSecond);
        }

        [Test]
        public void Total_per_second_uninitialized()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            Assert.AreEqual(decimal.Zero, measuredProgress.TotalPerSecond);
        }

        [Test]
        public void Current_value_uninitialized()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            Assert.AreEqual(0L, measuredProgress.CurrentValue);
        }

        [Test]
        public void Update_0L()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            measuredProgress.Update(0L);
            Assert.AreEqual(0L, measuredProgress.CurrentValue);
        }

        [Test]
        public void Update_0L_total_per_second()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            measuredProgress.Update(0L);
            Assert.AreEqual(0L, measuredProgress.TotalPerSecond);
        }

        [Test]
        public void Update_0L_current_per_second()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            measuredProgress.Update(0L);
            Assert.AreEqual(0L, measuredProgress.CurrentPerSecond);
        }

        [Test]
        [Retry(3)]
        public void Update_twice_total_per_second()
        {
            ManualTimestamper manualTimestamper = new ManualTimestamper();
            MeasuredProgress measuredProgress = new MeasuredProgress(manualTimestamper);
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
            ManualTimestamper manualTimestamper = new ManualTimestamper();
            MeasuredProgress measuredProgress = new MeasuredProgress(manualTimestamper);
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
            ManualTimestamper manualTimestamper = new ManualTimestamper();
            MeasuredProgress measuredProgress = new MeasuredProgress(manualTimestamper);
            measuredProgress.Update(10L);
            measuredProgress.SetMeasuringPoint();
            manualTimestamper.Add(TimeSpan.FromMilliseconds(100));
            measuredProgress.Update(20L);
            Assert.LessOrEqual(measuredProgress.CurrentPerSecond, 105M);
        }

        [Test]
        public void Update_thrice_result_per_second()
        {
            ManualTimestamper manualTimestamper = new ManualTimestamper();
            MeasuredProgress measuredProgress = new MeasuredProgress(manualTimestamper);
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
            ManualTimestamper manualTimestamper = new ManualTimestamper();
            MeasuredProgress measuredProgress = new MeasuredProgress(manualTimestamper);
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
            Assert.AreEqual(0M, measuredProgress.CurrentPerSecond);
        }

        [Test]
        public void Has_ended_returns_true_when_ended()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            measuredProgress.MarkEnd();
            Assert.True(measuredProgress.HasEnded);
        }
        
        [Test]
        public void Has_ended_returns_false_when_ended()
        {
            MeasuredProgress measuredProgress = new MeasuredProgress();
            Assert.False(measuredProgress.HasEnded);
        }
    }
}
