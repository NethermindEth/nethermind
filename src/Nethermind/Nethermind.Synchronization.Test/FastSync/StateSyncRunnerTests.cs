// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class StateSyncRunnerTests
{
    [Test]
    public async Task RunBalHealing_abandons_a_same_height_reorg()
    {
        BlockHeader previousPivot = Build.A.BlockHeader
            .WithNumber(100)
            .WithHash(Keccak.Compute("previous pivot"))
            .WithStateRoot(Keccak.Compute("previous root"))
            .WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp)
            .TestObject;
        BlockHeader reorgedPivot = Build.A.BlockHeader
            .WithNumber(100)
            .WithHash(Keccak.Compute("reorged pivot"))
            .WithStateRoot(Keccak.Compute("reorged root"))
            .WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp)
            .TestObject;

        IBalHealing balHealing = Substitute.For<IBalHealing>();
        balHealing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>()).Returns(previousPivot.StateRoot);

        IStateSyncPivot stateSyncPivot = Substitute.For<IStateSyncPivot>();
        stateSyncPivot.UpdatedStorages.Returns([]);
        stateSyncPivot.GetPivotHeader().Returns(reorgedPivot);
        stateSyncPivot.CanFinalize(previousPivot).Returns(true);

        StateSyncRunner runner = new(
            null!, balHealing, null!, stateSyncPivot, null!, null!,
            new SyncConfig { SnapSync = true }, null!, null!, null!, null!, null!, null!, LimboLogs.Instance);

        bool result = await runner.RunBalHealing(previousPivot, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.False);
            balHealing.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
        });
    }
}
