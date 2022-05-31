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
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Producers;
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
        [Retry(3)]
        public async Task should_cancel_block_production_trigger_on_next_step_if_not_finished_yet()
        {
            List<BlockProductionEventArgs> args = new();
            await using (BuildBlocksOnAuRaSteps buildBlocksOnAuRaSteps = new(new TestAuRaStepCalculator(), LimboLogs.Instance))
            {
                buildBlocksOnAuRaSteps.TriggerBlockProduction += (o, e) =>
                {
                    args.Add(e);
                    e.BlockProductionTask = TaskExt.DelayAtLeast(TestAuRaStepCalculator.StepDurationTimeSpan * 10, e.CancellationToken)
                        .ContinueWith(t => (Block?)null, e.CancellationToken);
                };

                while (args.Count < 4)
                {
                    await TaskExt.DelayAtLeast(TestAuRaStepCalculator.StepDurationTimeSpan);
                }
            }

            bool[] allButLastCancellations = args.Skip(1).SkipLast(1).Select(e => e.CancellationToken.IsCancellationRequested).ToArray();
            allButLastCancellations.Should().AllBeEquivalentTo(true);
            allButLastCancellations.Should().HaveCountGreaterOrEqualTo(2);
        }
        
        [Test]
        public async Task should_not_cancel_block_production_trigger_on_next_step_finished()
        {
            List<BlockProductionEventArgs> args = new();

            await using (BuildBlocksOnAuRaSteps buildBlocksOnAuRaSteps = new(new TestAuRaStepCalculator(), LimboLogs.Instance))
            {
                buildBlocksOnAuRaSteps.TriggerBlockProduction += (o, e) =>
                {
                    args.Add(e);
                };

                while (args.Count < 2)
                {
                    await TaskExt.DelayAtLeast(TestAuRaStepCalculator.StepDurationTimeSpan);
                }
            }

            IEnumerable<bool> enumerable = args.Select(e => e.CancellationToken.IsCancellationRequested);
            enumerable.Should().AllBeEquivalentTo(false);
        }
        
        private class TestAuRaStepCalculator : IAuRaStepCalculator
        {
            public const long StepDuration = 10;
            public static readonly TimeSpan StepDurationTimeSpan = TimeSpan.FromMilliseconds(StepDuration);
            
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
