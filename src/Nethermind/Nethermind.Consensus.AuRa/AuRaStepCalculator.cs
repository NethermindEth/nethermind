// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaStepCalculator : IAuRaStepCalculator
    {
        private readonly IList<StepDurationInfo> _stepDurations;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        public AuRaStepCalculator(IDictionary<long, long> stepDurations, ITimestamper timestamper, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<AuRaStepCalculator>() ?? throw new ArgumentNullException(nameof(logManager));
            ValidateStepDurations(stepDurations);
            _stepDurations = CreateStepDurations(stepDurations);
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        }

        public long CurrentStep
        {
            get
            {
                var timestampSeconds = _timestamper.UnixTime.SecondsLong;
                return GetStepInfo(timestampSeconds).GetCurrentStep(timestampSeconds);
            }
        }

        public TimeSpan TimeToNextStep => new TimeSpan(TimeToNextStepInTicks);

        public TimeSpan TimeToStep(long step)
        {
            var epoch = _timestamper.UnixTime;
            var currentStepInfo = GetStepInfo(epoch.SecondsLong);
            long currentStep = currentStepInfo.GetCurrentStep(epoch.SecondsLong);
            if (step <= currentStep)
            {
                return TimeSpan.Zero;
            }
            else
            {
                var timeToNextStep = new TimeSpan(GetTimeToNextStepInTicks(epoch, currentStepInfo));
                return timeToNextStep + TimeSpan.FromSeconds(currentStepInfo.StepDuration * (step - currentStep - 1));
            }

        }

        public long CurrentStepDuration
        {
            get
            {
                UnixTime epoch = _timestamper.UnixTime;
                return GetStepInfo(epoch.SecondsLong).StepDuration;
            }
        }

        private long TimeToNextStepInTicks
        {
            get
            {
                var unixTime = _timestamper.UnixTime;
                var currentStepInfo = GetStepInfo(unixTime.SecondsLong);
                return GetTimeToNextStepInTicks(unixTime, currentStepInfo);
            }
        }

        private static long GetTimeToNextStepInTicks(UnixTime unixTime, StepDurationInfo currentStepInfo)
        {
            var timeFromTransition = unixTime.MillisecondsLong - currentStepInfo.TransitionTimestampMilliseconds;
            var timeAlreadyPassedToNextStep = timeFromTransition % currentStepInfo.StepDurationMilliseconds;
            return (currentStepInfo.StepDurationMilliseconds - timeAlreadyPassedToNextStep) * TimeSpan.TicksPerMillisecond;
        }

        private StepDurationInfo GetStepInfo(long timestampInSeconds) =>
            _stepDurations.TryGetForActivation(timestampInSeconds, out var currentStepInfo)
                ? currentStepInfo
                : throw new InvalidOperationException($"Couldn't find state step duration information at timestamp {timestampInSeconds}");

        private void ValidateStepDurations(IDictionary<long, long> stepDurations)
        {
            if (stepDurations?.ContainsKey(0) != true)
            {
                throw new ArgumentException("Authority Round step 0 duration is undefined.");
            }

            if (stepDurations.Any(s => s.Value == 0))
            {
                throw new ArgumentException("Authority Round step duration cannot be 0.");
            }

            foreach (var key in stepDurations.Keys.ToArray())
            {
                const ushort maxValue = UInt16.MaxValue;

                if (stepDurations[key] > maxValue)
                {
                    if (_logger.IsWarn) _logger.Warn($"Step duration is too high ({stepDurations[key]}), setting it to {maxValue}");
                    stepDurations[key] = maxValue;
                }
            }
        }

        private IList<StepDurationInfo> CreateStepDurations(IDictionary<long, long> stepDurations)
        {
            StepDurationInfo[] result = new StepDurationInfo[stepDurations.Count];
            KeyValuePair<long, long> firstStep = stepDurations.First();
            int index = 0;
            var previousStep = result[index++] = new StepDurationInfo(0, firstStep.Key, firstStep.Value);
            foreach (var currentStep in stepDurations.Skip(1))
            {
                var previousStepLength = currentStep.Key - previousStep.TransitionTimestamp;
                var previousStepCount = previousStepLength / previousStep.StepDuration + (previousStepLength % previousStep.StepDuration > 0 ? 1 : 0);
                var currentTransitionStep = previousStep.TransitionStep + previousStepCount;
                var currentTransitionTimestamp = previousStep.TransitionTimestamp + previousStepCount * previousStep.StepDuration;
                previousStep = result[index++] = new StepDurationInfo(currentTransitionStep, currentTransitionTimestamp, currentStep.Value);
            }
            return result;
        }

        private class StepDurationInfo : IActivatedAt
        {
            public StepDurationInfo(long transitionStep, long transitionTimestamp, long stepDuration)
            {
                const long millisecondsInSecond = 1000;

                TransitionStep = transitionStep;
                TransitionTimestamp = transitionTimestamp;
                StepDuration = stepDuration;
                StepDurationMilliseconds = stepDuration * millisecondsInSecond;
                TransitionTimestampMilliseconds = transitionTimestamp * millisecondsInSecond;
            }

            public long TransitionStep { get; }
            public long TransitionTimestamp { get; }
            public long TransitionTimestampMilliseconds { get; }
            public long StepDuration { get; }
            public long StepDurationMilliseconds { get; }
            long IActivatedAt<long>.Activation => TransitionTimestamp;

            public long GetCurrentStep(in long timestampSeconds) => TransitionStep + (timestampSeconds - TransitionTimestamp) / StepDuration;
        }
    }
}
