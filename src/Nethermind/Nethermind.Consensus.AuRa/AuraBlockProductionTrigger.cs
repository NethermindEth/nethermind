// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class BuildBlocksOnAuRaSteps : BuildBlocksInALoop
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;

        public BuildBlocksOnAuRaSteps(IAuRaStepCalculator auRaStepCalculator, ILogManager logManager, bool autoStart = true) : base(logManager, false)
        {
            _auRaStepCalculator = auRaStepCalculator;
            if (autoStart)
            {
                StartLoop();
            }
        }

        protected override async Task ProducerLoopStep(CancellationToken token)
        {
            // be able to cancel current step block production if needed
            using CancellationTokenSource stepTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);

            TimeSpan timeToNextStep = _auRaStepCalculator.TimeToNextStep;
            Task delayToNextStep = TaskExt.DelayAtLeast(timeToNextStep, token);

            // try produce a block
            Task produceBlockInCurrentStep = base.ProducerLoopStep(stepTokenSource.Token);

            // wait for next step
            if (Logger.IsDebug) Logger.Debug($"Waiting {timeToNextStep} for next AuRa step.");
            await delayToNextStep;

            // if block production of now previous step wasn't completed, lets cancel it 
            if (!produceBlockInCurrentStep.IsCompleted)
            {
                stepTokenSource.Cancel();
            }
        }
    }
}
