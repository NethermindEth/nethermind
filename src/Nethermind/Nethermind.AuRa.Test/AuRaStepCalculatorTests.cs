//  Copyright (c) 2018 Demerzel Solutions Limited
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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaStepCalculatorTests
    {
        [TestCase(2)]
        [TestCase(1)]
        [Retry(3)]
        public async Task step_increases_after_timeToNextStep(int stepDuration)
        {
            var calculator = new AuRaStepCalculator(stepDuration, new Timestamper());
            var step = calculator.CurrentStep;
            await TaskExt.DelayAtLeast(calculator.TimeToNextStep);
            calculator.CurrentStep.Should().Be(step + 1, calculator.TimeToNextStep.ToString());
        }
        
        [TestCase(2)]
        [TestCase(1)]
        [Retry(3)]
        public async Task after_waiting_for_next_step_timeToNextStep_should_be_close_to_stepDuration_in_seconds(int stepDuration)
        {
            var calculator = new AuRaStepCalculator(stepDuration, new Timestamper());
            await TaskExt.DelayAtLeast(calculator.TimeToNextStep);
            calculator.TimeToNextStep.Should().BeCloseTo(TimeSpan.FromSeconds(stepDuration), TimeSpan.FromMilliseconds(200));
        }
        
        [TestCase(100000060005L, 2)]
        [Retry(3)]
        public void step_is_calculated_correctly(long milliSeconds, int stepDuration)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(milliSeconds);
            var calculator = new AuRaStepCalculator(stepDuration, new Timestamper(time.UtcDateTime));
            calculator.CurrentStep.Should().Be(time.ToUnixTimeSeconds() / stepDuration);
        }
    }
}