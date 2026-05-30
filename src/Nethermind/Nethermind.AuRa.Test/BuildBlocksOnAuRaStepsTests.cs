// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        public async Task should_cancel_block_production_trigger_on_next_step_if_not_finished_yet()
        {
            List<BlockProductionEventArgs> args = [];
            using SemaphoreSlim eventReceived = new(0);
            await using (BuildBlocksOnAuRaSteps buildBlocksOnAuRaSteps = new(new TestAuRaStepCalculator(), LimboLogs.Instance))
            {
                buildBlocksOnAuRaSteps.TriggerBlockProduction += (o, e) =>
                {
                    args.Add(e);
                    e.BlockProductionTask = TaskExt.DelayAtLeast(TestAuRaStepCalculator.StepDurationTimeSpan * 10, e.CancellationToken)
                        .ContinueWith(t => (Block?)null, e.CancellationToken);
                    eventReceived.Release();
                };

                for (int i = 0; i < 4; i++)
                {
                    Assert.That(await eventReceived.WaitAsync(TimeSpan.FromSeconds(30)), Is.True, $"Trigger event #{i + 1} did not arrive within 30s");
                }
            }

            bool[] allButLastCancellations = args.Skip(1).SkipLast(1).Select(e => e.CancellationToken.IsCancellationRequested).ToArray();
            Assert.That(allButLastCancellations, Is.All.EqualTo(true));
            Assert.That(allButLastCancellations.Length, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public async Task should_not_cancel_block_production_trigger_on_next_step_finished()
        {
            List<BlockProductionEventArgs> args = [];
            using SemaphoreSlim eventReceived = new(0);

            BuildBlocksOnAuRaSteps buildBlocksOnAuRaSteps = new(new TestAuRaStepCalculator(), LimboLogs.Instance);
            buildBlocksOnAuRaSteps.TriggerBlockProduction += (o, e) =>
            {
                args.Add(e);
                eventReceived.Release();
            };

            for (int i = 0; i < 2; i++)
            {
                Assert.That(await eventReceived.WaitAsync(TimeSpan.FromSeconds(30)), Is.True);
            }

            IEnumerable<bool> enumerable = args.Select(e => e.CancellationToken.IsCancellationRequested).ToArray();
            Assert.That(enumerable, Is.All.EqualTo(false));

            await buildBlocksOnAuRaSteps.DisposeAsync();
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

            public TimeSpan TimeToStep(long step) =>
                throw new NotImplementedException();

            public long CurrentStepDuration => throw new NotImplementedException();

            private UnixTime UnixTime => new(DateTimeOffset.UtcNow);
        }
    }
}
