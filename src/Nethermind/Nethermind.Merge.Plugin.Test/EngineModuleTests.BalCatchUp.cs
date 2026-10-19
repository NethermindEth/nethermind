// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs.Forks;
using Autofac;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    [NonParallelizable]
    public async Task NewPayloadV5_executes_catch_up_blocks_and_counts_them_when_unfinalized([Values] bool catchingUp)
    {
        // The processor reads the clock through ITimestamper, which the test chain otherwise leaves on the system clock.
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance,
            configurer: static builder => builder.AddSingleton<ITimestamper>(static ctx => ctx.Resolve<ManualTimestamper>()));
        BlockHeader head = chain.BlockTree.Head!.Header;
        // The payload below takes the head's timestamp plus one; move the clock so it is either just past or at the age
        // from which a block counts as caught up.
        ulong age = FinalizedBlockAccessListProcessor.CatchUpAgeSlots * new BlocksConfig().SecondsPerSlot + (catchingUp ? 1UL : 0UL);
        chain.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)(head.Timestamp + 1 + age)).UtcDateTime);
        long notFinalizedBefore = Metrics.BalCatchUpFallbacks.GetValueOrDefault(BalCatchUpFallbackReason.NotFinalized);
        long reconstructedBefore = Metrics.BalCatchUpBlocks;

        ExecutionPayloadV4 payload = await AddNewBlockV6(chain.EngineRpcModule, chain, transactionCount: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(payload.BlockHash), "the payload was validated and became the head");
            Assert.That(Metrics.BalCatchUpBlocks - reconstructedBefore, Is.Zero, "engine payloads always execute");
            Assert.That(Metrics.BalCatchUpFallbacks.GetValueOrDefault(BalCatchUpFallbackReason.NotFinalized) - notFinalizedBefore,
                catchingUp ? Is.Positive : Is.Zero, "the payload is above the finalized block the forkchoice reported");
        }
    }
}
