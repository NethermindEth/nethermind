// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    // The counters are static and other tests run alongside, so this checks the chain's own log of the decision.
    [Test]
    public async Task NewPayloadV5_executes_catch_up_blocks_and_counts_them_when_unfinalized([Values] bool catchingUp, [Values] bool enableCatchUp)
    {
        TestLogger catchUpLog = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance, configurer: builder => builder
            .AddSingleton<ISyncConfig>(new SyncConfig { ReconstructFinalizedStateFromBlockAccessLists = enableCatchUp })
            // The processor reads the clock through ITimestamper, which the test chain otherwise leaves on the system clock.
            .AddSingleton<ITimestamper>(static ctx => ctx.Resolve<ManualTimestamper>())
            .AddDecorator<ILogManager>((_, inner) => new CatchUpLogManager(inner, catchUpLog)));
        BlockHeader head = chain.BlockTree.Head!.Header;
        // The payload below takes the head's timestamp plus one; move the clock so it is either just past or at the age
        // from which a block counts as caught up.
        ulong age = FinalizedBlockAccessListProcessor.CatchUpAgeSlots * new BlocksConfig().SecondsPerSlot + (catchingUp ? 1UL : 0UL);
        chain.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)(head.Timestamp + 1 + age)).UtcDateTime);

        ExecutionPayloadV4 payload = await AddNewBlockV6(chain.EngineRpcModule, chain, transactionCount: 1);

        string[] executed = [.. catchUpLog.LogList.Where(static l => l.StartsWith("Executing catch-up", StringComparison.Ordinal))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(payload.BlockHash), "the payload was validated and became the head");
            Assert.That(catchUpLog.LogList, Has.None.StartsWith("Reconstructed"), "engine payloads always execute");
            Assert.That(executed, catchingUp && enableCatchUp ? Has.Exactly(1).Contains($"{payload.BlockNumber}").And.Contains(nameof(BalCatchUpFallbackReason.NotFinalized)) : Is.Empty,
                "the payload is above the finalized block the forkchoice reported");
        }
    }

    private sealed class CatchUpLogManager(ILogManager inner, TestLogger catchUpLog) : ILogManager
    {
        private static readonly string CatchUpLogger = ILogManager.GetLoggerName(typeof(FinalizedBlockAccessListProcessor));

        public ILogger GetClassLogger<T>() => GetLogger(ILogManager.GetLoggerName(typeof(T)));

        public ILogger GetLogger(string loggerName) => loggerName == CatchUpLogger ? new ILogger(catchUpLog) : inner.GetLogger(loggerName);
    }
}
