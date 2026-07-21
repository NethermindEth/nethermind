// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaStepCalculatorTests
    {
        [TestCase(2)]
        [TestCase(1)]
        public void step_increases_after_timeToNextStep(int stepDuration)
        {
            ManualTimestamper manualTimestamper = new(DateTime.UtcNow);
            AuRaStepCalculator calculator = new(GetStepDurationsForSingleStep(stepDuration), manualTimestamper, LimboLogs.Instance);
            ulong step = calculator.CurrentStep;
            manualTimestamper.Add(calculator.TimeToNextStep);
            Assert.That(calculator.CurrentStep, Is.EqualTo(step + 1), calculator.TimeToNextStep.ToString());
        }

        [TestCase(2)]
        [TestCase(1)]
        public void after_waiting_for_next_step_timeToNextStep_should_be_close_to_stepDuration_in_seconds(int stepDuration)
        {
            ManualTimestamper manualTimestamper = new(DateTime.UtcNow);
            AuRaStepCalculator calculator = new(GetStepDurationsForSingleStep(stepDuration), manualTimestamper, LimboLogs.Instance);
            manualTimestamper.Add(calculator.TimeToNextStep);
            Assert.That(calculator.TimeToNextStep, Is.EqualTo(TimeSpan.FromSeconds(stepDuration)).Within(TimeSpan.FromMilliseconds(200)));
        }

        [TestCase(100000060005L, 2)]
        public void step_is_calculated_correctly(long milliSeconds, int stepDuration)
        {
            DateTimeOffset time = DateTimeOffset.FromUnixTimeMilliseconds(milliSeconds);
            ManualTimestamper timestamper = new(time.UtcDateTime);
            AuRaStepCalculator calculator = new(GetStepDurationsForSingleStep(stepDuration), timestamper, LimboLogs.Instance);
            Assert.That(calculator.CurrentStep, Is.EqualTo((ulong)(time.ToUnixTimeSeconds() / stepDuration)));
        }

        [TestCase(100000060005L, 2, 50000030UL)]
        [TestCase(100000060005L, 2, 50000000UL)]
        [TestCase(100000060005L, 2, 50000031UL)]
        [TestCase(100000060005L, 2, 50000035UL)]
        public void time_to_step_is_calculated_correctly(long milliSeconds, int stepDuration, ulong checkedStep)
        {
            const ulong currentStep = 50000030UL;
            TimeSpan timeToNextStep = TimeSpan.FromMilliseconds(1995);
            DateTimeOffset time = DateTimeOffset.FromUnixTimeMilliseconds(milliSeconds);
            ManualTimestamper timestamper = new(time.UtcDateTime);
            AuRaStepCalculator calculator = new(GetStepDurationsForSingleStep(stepDuration), timestamper, LimboLogs.Instance);
            TimeSpan expected = checkedStep <= currentStep ? TimeSpan.FromMilliseconds(0) : TimeSpan.FromSeconds((double)(checkedStep - currentStep - 1) * stepDuration) + timeToNextStep;
            TestContext.Out.WriteLine($"Expected time to step {checkedStep} is {expected}");
            Assert.That(calculator.TimeToStep(checkedStep), Is.EqualTo(expected));
        }

        public static IEnumerable StepDurationsTests
        {
            get
            {
                IList<(long SecondsOffset, long StepDuration)> secondsDurations = [(0, 5), (7, 7), (25, 10)];
                yield return new TestCaseData(secondsDurations, 0, BaseOffset / 5, 5) { TestName = "0 seconds" };
                yield return new TestCaseData(secondsDurations, 3, BaseOffset / 5, 2) { TestName = "3 seconds" };
                yield return new TestCaseData(secondsDurations, 10, BaseOffset / 5 + 2, 7) { TestName = "10 seconds" };
                yield return new TestCaseData(secondsDurations, 16, BaseOffset / 5 + 2, 1) { TestName = "16 seconds" };
                yield return new TestCaseData(secondsDurations, 18, BaseOffset / 5 + 2 + 1, 6) { TestName = "18 seconds" };
                yield return new TestCaseData(secondsDurations, 27, BaseOffset / 5 + 2 + 2, 4) { TestName = "27 seconds" };
                yield return new TestCaseData(secondsDurations, 31, BaseOffset / 5 + 2 + 3, 10) { TestName = "31 seconds" };
                yield return new TestCaseData(secondsDurations, 56, BaseOffset / 5 + 2 + 5, 5) { TestName = "56 seconds" };
            }
        }

        private const long BaseOffset = 300000000;

        [TestCaseSource(nameof(StepDurationsTests))]
        public void step_are_calculated_correctly(IList<(long SecondsOffset, long StepDuration)> secondsDurations, long second, long expectedStep, long expectedSecondsToNextStep)
        {
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(BaseOffset);
            ManualTimestamper timestamper = new(now.UtcDateTime);
            Dictionary<ulong, long> stepDurations = secondsDurations.ToDictionary(
                kvp => kvp.SecondsOffset == 0 ? 0UL : (ulong)(kvp.SecondsOffset + now.ToUnixTimeSeconds()),
                kvp => kvp.StepDuration);
            AuRaStepCalculator calculator = new(stepDurations, timestamper, LimboLogs.Instance);
            timestamper.Add(TimeSpan.FromSeconds(second));
            Assert.That(calculator.CurrentStep, Is.EqualTo(expectedStep));
            Assert.That(calculator.TimeToNextStep, Is.EqualTo(TimeSpan.FromSeconds(expectedSecondsToNextStep)));
        }

        private IDictionary<ulong, long> GetStepDurationsForSingleStep(long stepDuration) =>
            new Dictionary<ulong, long>() { { 0UL, stepDuration } };
    }
}
