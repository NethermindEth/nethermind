// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterBlockImprovementContextTests
{
    [Test]
    [NonParallelizable]
    public async Task Counts_every_missed_key_when_contexts_time_out_concurrently()
    {
        const int contexts = 10_000;
        const ulong slotTimestamp = ShutterTestsCommon.InitialSlotTimestamp;

        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer.BuildBlock(default, default, default, default, default).ReturnsForAnyArgs(Task.FromResult<Block?>(null));
        SlotTime slotTime = new(0, new Timestamper(DateTimeOffset.FromUnixTimeSeconds((long)slotTimestamp).UtcDateTime), ShutterTestsCommon.SlotLength, TimeSpan.Zero);
        Block block = Build.A.Block.TestObject;
        PayloadAttributes payloadAttributes = new() { Timestamp = slotTimestamp };

        ulong missedBefore = Metrics.ShutterKeysMissed;

        ShutterBlockImprovementContext[] improvements = Enumerable.Range(0, contexts).Select(_ =>
        {
            CancellationTokenSource cts = new();
            cts.Cancel();
            return new ShutterBlockImprovementContext(
                blockProducer,
                Substitute.For<IShutterTxSignal>(),
                new ShutterConfig(),
                slotTime,
                block,
                block.Header,
                payloadAttributes,
                DateTimeOffset.UtcNow,
                ShutterTestsCommon.SlotLength,
                LimboLogs.Instance,
                new SharedCancellationTokenSource(cts));
        }).ToArray();

        await Task.WhenAll(improvements.Select(static i => i.ImprovementTask));

        Assert.That(Metrics.ShutterKeysMissed - missedBefore, Is.EqualTo((ulong)contexts));
    }
}
