// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.StateDiffArchive.Data;
using Nethermind.StateDiffArchive.Replay;
using Nethermind.StateDiffArchive.Storage;
using NUnit.Framework;

namespace Nethermind.StateDiffArchive.Test;

[Parallelizable(ParallelScope.Self)]
public class ReplayPrewarmGateTests
{
    [Test]
    public void Skips_prewarm_for_recorded_block_and_delegates_otherwise()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sds-gate-{Guid.NewGuid():N}");
        try
        {
            using StateDiffStore store = new(
                new StateDiffArchiveConfig { ArchivePath = dir, ReplayEnabled = true },
                new InitConfig(),
                LimboLogs.Instance);
            store.Write(new StateDiffRecordBuilder(), 5, TestItem.KeccakA); // a diff exists for block 5, not for 6

            CountingPreWarmer inner = new();
            ReplayPrewarmGate gate = new(inner, store);

            gate.PreWarmCaches(Build.A.Block.WithNumber(5).TestObject, null, Cancun.Instance);
            gate.PreWarmCaches(Build.A.Block.WithNumber(6).TestObject, null, Cancun.Instance);

            Assert.That(inner.Calls, Is.EqualTo(1), "prewarm skipped for the replayed block, delegated for the other");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private sealed class CountingPreWarmer : IBlockCachePreWarmer
    {
        public int Calls;

        public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public CacheType ClearCaches() => default;
        public bool IsBalReadWarmingEnabled(IReleaseSpec spec) => false;
        public void Dispose() { }
    }
}
