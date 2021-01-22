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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.AuRa.Test
{
    public class AuRaStepCalculatorTests
    {
        [TestCase(2)]
        [TestCase(1)]
        public void step_increases_after_timeToNextStep(int stepDuration)
        {
            var manualTimestamper = new ManualTimestamper(DateTime.Now);
            var calculator = new AuRaStepCalculator(GetStepDurationsForSingleStep(stepDuration), manualTimestamper, LimboLogs.Instance);
            var step = calculator.CurrentStep;
            manualTimestamper.Add(calculator.TimeToNextStep);
            calculator.CurrentStep.Should().Be(step + 1, calculator.TimeToNextStep.ToString());
        }
        
        [TestCase(2)]
        [TestCase(1)]
        public void after_waiting_for_next_step_timeToNextStep_should_be_close_to_stepDuration_in_seconds(int stepDuration)
        {
            var manualTimestamper = new ManualTimestamper(DateTime.Now);
            var calculator = new AuRaStepCalculator(GetStepDurationsForSingleStep(stepDuration), manualTimestamper, LimboLogs.Instance);
            manualTimestamper.Add(calculator.TimeToNextStep);
            calculator.TimeToNextStep.Should().BeCloseTo(TimeSpan.FromSeconds(stepDuration), TimeSpan.FromMilliseconds(200));
        }
        
        [TestCase(100000060005L, 2)]
        public void step_is_calculated_correctly(long milliSeconds, int stepDuration)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(milliSeconds);
            var timestamper = new ManualTimestamper(time.UtcDateTime);
            var calculator = new AuRaStepCalculator(GetStepDurationsForSingleStep(stepDuration), timestamper, LimboLogs.Instance);
            calculator.CurrentStep.Should().Be(time.ToUnixTimeSeconds() / stepDuration);
        }
        
        [TestCase(100000060005L, 2, 50000030)]
        [TestCase(100000060005L, 2, 50000000)]
        [TestCase(100000060005L, 2, 50000031)]
        [TestCase(100000060005L, 2, 50000035)]
        public void time_to_step_is_calculated_correctly(long milliSeconds, int stepDuration, int checkedStep)
        {
            const long currentStep = 50000030;
            TimeSpan timeToNextStep = TimeSpan.FromMilliseconds(1995);
            var time = DateTimeOffset.FromUnixTimeMilliseconds(milliSeconds);
            var timestamper = new ManualTimestamper(time.UtcDateTime);
            var calculator = new AuRaStepCalculator(GetStepDurationsForSingleStep(stepDuration), timestamper, LimboLogs.Instance);
            TimeSpan expected = checkedStep <= currentStep ? TimeSpan.FromMilliseconds(0) : TimeSpan.FromSeconds((checkedStep - currentStep - 1) * stepDuration) + timeToNextStep;
            TestContext.Out.WriteLine($"Expected time to step {checkedStep} is {expected}");
            calculator.TimeToStep(checkedStep).Should().Be(expected);
        }
        
        public static IEnumerable StepDurationsTests
        {
            get
            {
                IList<(long SecondsOffset, long StepDuration)> secondsDurations = new List<(long SecondsOffset, long StepDuration)>() {(0, 5), (7, 7), (25, 10)};
                yield return new TestCaseData(secondsDurations, 0, BaseOffset / 5, 5) {TestName = "0 seconds"};
                yield return new TestCaseData(secondsDurations, 3, BaseOffset / 5, 2) {TestName = "3 seconds"};
                yield return new TestCaseData(secondsDurations, 10, BaseOffset / 5 + 2, 7) {TestName = "10 seconds"};
                yield return new TestCaseData(secondsDurations, 16, BaseOffset / 5 + 2, 1) {TestName = "16 seconds"};
                yield return new TestCaseData(secondsDurations, 18, BaseOffset / 5 + 2 + 1, 6) {TestName = "18 seconds"};
                yield return new TestCaseData(secondsDurations, 27, BaseOffset / 5 + 2 + 2, 4) {TestName = "27 seconds"};
                yield return new TestCaseData(secondsDurations, 31, BaseOffset / 5 + 2 + 3, 10) {TestName = "31 seconds"};
                yield return new TestCaseData(secondsDurations, 56, BaseOffset / 5 + 2 + 5, 5) {TestName = "56 seconds"};
            }
        }

        private const long BaseOffset = 300000000;
        
        [TestCaseSource(nameof(StepDurationsTests))]
        public void step_are_calculated_correctly(IList<(long SecondsOffset, long StepDuration)> secondsDurations, long second, long expectedStep, long expectedSecondsToNextStep)
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(BaseOffset);
            var timestamper = new ManualTimestamper(now.UtcDateTime);
            var stepDurations = secondsDurations.ToDictionary(
                kvp => kvp.SecondsOffset == 0 ? 0 : kvp.SecondsOffset + now.ToUnixTimeSeconds(), 
                kvp => kvp.StepDuration);
            var calculator = new AuRaStepCalculator(stepDurations, timestamper, LimboLogs.Instance);
            timestamper.Add(TimeSpan.FromSeconds(second));
            calculator.CurrentStep.Should().Be(expectedStep);
            calculator.TimeToNextStep.Should().Be(TimeSpan.FromSeconds(expectedSecondsToNextStep));
        }

        private IDictionary<long, long> GetStepDurationsForSingleStep(long stepDuration) => new Dictionary<long, long>() {{0, stepDuration}};
    }
}
