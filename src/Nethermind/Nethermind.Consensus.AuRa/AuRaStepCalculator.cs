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

        public AuRaStepCalculator(IDictionary<ulong, long> stepDurations, ITimestamper timestamper, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<AuRaStepCalculator>() ?? throw new ArgumentNullException(nameof(logManager));
            ValidateStepDurations(stepDurations);
            _stepDurations = CreateStepDurations(stepDurations);
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        }

        public ulong CurrentStep
        {
            get
            {
                ulong timestampSeconds = _timestamper.UnixTime.Seconds;
                return GetStepInfo(timestampSeconds).GetCurrentStep(timestampSeconds);
            }
        }

        public TimeSpan TimeToNextStep => new(TimeToNextStepInTicks);

        public TimeSpan TimeToStep(ulong step)
        {
            UnixTime epoch = _timestamper.UnixTime;
            StepDurationInfo currentStepInfo = GetStepInfo(epoch.Seconds);
            ulong currentStep = currentStepInfo.GetCurrentStep(epoch.Seconds);
            if (step <= currentStep)
            {
                return TimeSpan.Zero;
            }
            else
            {
                TimeSpan timeToNextStep = new(GetTimeToNextStepInTicks(epoch, currentStepInfo));
                return timeToNextStep + TimeSpan.FromSeconds((long)currentStepInfo.StepDuration * (long)(step - currentStep - 1));
            }
        }

        public ulong CurrentStepDuration
        {
            get
            {
                UnixTime epoch = _timestamper.UnixTime;
                return GetStepInfo(epoch.Seconds).StepDuration;
            }
        }

        private long TimeToNextStepInTicks
        {
            get
            {
                UnixTime unixTime = _timestamper.UnixTime;
                StepDurationInfo currentStepInfo = GetStepInfo(unixTime.Seconds);
                return GetTimeToNextStepInTicks(unixTime, currentStepInfo);
            }
        }

        private static long GetTimeToNextStepInTicks(UnixTime unixTime, StepDurationInfo currentStepInfo)
        {
            ulong milliseconds = unixTime.Milliseconds;
            ulong transition = currentStepInfo.TransitionTimestampMilliseconds;
            ulong timeFromTransition = milliseconds >= transition ? milliseconds - transition : 0UL;
            ulong timeAlreadyPassedToNextStep = timeFromTransition % currentStepInfo.StepDurationMilliseconds;
            return (long)(currentStepInfo.StepDurationMilliseconds - timeAlreadyPassedToNextStep) * TimeSpan.TicksPerMillisecond;
        }

        private StepDurationInfo GetStepInfo(ulong timestampInSeconds) =>
            _stepDurations.TryGetForActivation(timestampInSeconds, out StepDurationInfo currentStepInfo)
                ? currentStepInfo
                : throw new InvalidOperationException($"Couldn't find state step duration information at timestamp {timestampInSeconds}");

        private void ValidateStepDurations(IDictionary<ulong, long> stepDurations)
        {
            if (stepDurations?.ContainsKey(0) != true)
            {
                throw new ArgumentException("Authority Round step 0 duration is undefined.");
            }

            if (stepDurations.Any(static s => s.Value == 0))
            {
                throw new ArgumentException("Authority Round step duration cannot be 0.");
            }

            foreach (ulong key in stepDurations.Keys.ToArray())
            {
                const ushort maxValue = UInt16.MaxValue;

                if (stepDurations[key] > maxValue)
                {
                    if (_logger.IsWarn) _logger.Warn($"Step duration is too high ({stepDurations[key]}), setting it to {maxValue}");
                    stepDurations[key] = maxValue;
                }
            }
        }

        private static IList<StepDurationInfo> CreateStepDurations(IDictionary<ulong, long> stepDurations)
        {
            StepDurationInfo[] result = new StepDurationInfo[stepDurations.Count];
            KeyValuePair<ulong, long> firstStep = stepDurations.First();
            int index = 0;
            StepDurationInfo previousStep = result[index++] = new StepDurationInfo(0, firstStep.Key, (ulong)firstStep.Value);
            foreach (KeyValuePair<ulong, long> currentStep in stepDurations.Skip(1))
            {
                ulong previousStepLength = currentStep.Key - previousStep.TransitionTimestamp;
                ulong previousStepCount = previousStepLength / previousStep.StepDuration + (previousStepLength % previousStep.StepDuration > 0 ? 1UL : 0UL);
                ulong currentTransitionStep = previousStep.TransitionStep + previousStepCount;
                ulong currentTransitionTimestamp = previousStep.TransitionTimestamp + previousStepCount * previousStep.StepDuration;
                previousStep = result[index++] = new StepDurationInfo(currentTransitionStep, currentTransitionTimestamp, (ulong)currentStep.Value);
            }
            return result;
        }

        private class StepDurationInfo : IActivatedAt<ulong>
        {
            public StepDurationInfo(ulong transitionStep, ulong transitionTimestamp, ulong stepDuration)
            {
                const ulong millisecondsInSecond = 1000UL;

                TransitionStep = transitionStep;
                TransitionTimestamp = transitionTimestamp;
                StepDuration = stepDuration;
                StepDurationMilliseconds = stepDuration * millisecondsInSecond;
                TransitionTimestampMilliseconds = transitionTimestamp * millisecondsInSecond;
            }

            public ulong TransitionStep { get; }
            public ulong TransitionTimestamp { get; }
            public ulong TransitionTimestampMilliseconds { get; }
            public ulong StepDuration { get; }
            public ulong StepDurationMilliseconds { get; }
            ulong IActivatedAt<ulong>.Activation => TransitionTimestamp;

            public ulong GetCurrentStep(in ulong timestampSeconds) => TransitionStep + (timestampSeconds - TransitionTimestamp) / StepDuration;
        }
    }
}
