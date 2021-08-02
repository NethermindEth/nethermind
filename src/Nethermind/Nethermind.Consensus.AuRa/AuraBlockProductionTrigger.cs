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
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Producers;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class BuildBlocksOnAuRaSteps : BuildBlocksInALoop
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;

        public BuildBlocksOnAuRaSteps(ILogManager logManager, IAuRaStepCalculator auRaStepCalculator) : base(logManager)
        {
            _auRaStepCalculator = auRaStepCalculator;
        }

        protected override async Task ProducerLoopStep(CancellationToken token)
        {
            await base.ProducerLoopStep(token);
            TimeSpan timeToNextStep = _auRaStepCalculator.TimeToNextStep;
            if (Logger.IsDebug) Logger.Debug($"Waiting {timeToNextStep} for next AuRa step.");
            await TaskExt.DelayAtLeast(timeToNextStep, token);
        }
    }
}
