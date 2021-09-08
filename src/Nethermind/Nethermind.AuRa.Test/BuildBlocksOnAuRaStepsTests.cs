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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using FluentAssertions;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.AuRa.Test
{
    [Parallelizable(ParallelScope.All)]
    public class BuildBlocksOnAuRaStepsTests
    {
        [Test]
        public async Task should_cancel_block_production_trigger_on_next_step_if_not_finished_yet()
        {
            List<BlockProductionEventArgs> args = new();
            using (BuildBlocksOnAuRaSteps buildBlocksOnAuRaSteps = new(LimboLogs.Instance, new TestAuRaStepCalculator()))
            {
                buildBlocksOnAuRaSteps.TriggerBlockProduction += (o, e) =>
                {
                    args.Add(e);
                    e.BlockProductionTask = Task.Delay(TimeSpan.FromMilliseconds(TestAuRaStepCalculator.StepDuration * 3))
                        .ContinueWith(t => (Block?)null);
                };

                await TaskExt.DelayAtLeast(TimeSpan.FromMilliseconds(TestAuRaStepCalculator.StepDuration * 20));
            }

            args.Should().HaveCountGreaterOrEqualTo(2);
            IEnumerable<bool> enumerable = args.SkipLast(1).Select(e => e.CancellationToken.IsCancellationRequested);
            enumerable.Should().AllBeEquivalentTo(true);
            args[^1].CancellationToken.IsCancellationRequested.Should().BeFalse();
        }
        
        [Test]
        public async Task should_not_cancel_block_production_trigger_on_next_step_finished()
        {
            List<BlockProductionEventArgs> args = new();
            
            using (BuildBlocksOnAuRaSteps buildBlocksOnAuRaSteps = new(LimboLogs.Instance, new TestAuRaStepCalculator()))
            {
                buildBlocksOnAuRaSteps.TriggerBlockProduction += (o, e) =>
                {
                    args.Add(e);
                };

                await TaskExt.DelayAtLeast(TimeSpan.FromMilliseconds(TestAuRaStepCalculator.StepDuration * 20));
            }

            IEnumerable<bool> enumerable = args.Select(e => e.CancellationToken.IsCancellationRequested);
            enumerable.Should().AllBeEquivalentTo(false);
        }
        
        private class TestAuRaStepCalculator : IAuRaStepCalculator
        {
            public const long StepDuration = 10;
            public long CurrentStep => UnixTime.MillisecondsLong / StepDuration;

            public TimeSpan TimeToNextStep
            {
                get
                {
                    long milliseconds = UnixTime.MillisecondsLong;
                    return TimeSpan.FromMilliseconds(((milliseconds / StepDuration) + 1) * StepDuration - milliseconds);
                }
            }

            public TimeSpan TimeToStep(long step)
            {
                throw new NotImplementedException();
            }

            public long CurrentStepDuration => throw new NotImplementedException();

            private UnixTime UnixTime => new(DateTimeOffset.UtcNow);
        }
    }
}
