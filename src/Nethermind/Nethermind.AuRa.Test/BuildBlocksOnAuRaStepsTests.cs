// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

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
        [Retry(1024)]
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
