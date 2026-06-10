// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListManagerTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private static BlockAccessListManager CreateManager() => new(
        Substitute.For<IWorldState>(),
        Substitute.For<ISpecProvider>(),
        Substitute.For<IBlockhashProvider>(),
        LimboLogs.Instance,
        new BlocksConfig(),
        Substitute.For<IWithdrawalProcessorFactory>());

    [Test]
    public void DrainBalReadHint_blocks_until_tracked_hint_completes()
    {
        BlockAccessListManager manager = CreateManager();
        TaskCompletionSource hint = new(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TrackBalReadHint(hint.Task);

        Task drain = Task.Run(manager.DrainBalReadHint);

        Assert.That(drain.Wait(TimeSpan.FromMilliseconds(50)), Is.False);
        hint.SetResult();
        Assert.That(drain.Wait(DrainTimeout), Is.True);
    }

    private static IEnumerable<TestCaseData> NonSuccessfulHints()
    {
        yield return new TestCaseData(Task.FromCanceled(new CancellationToken(canceled: true))).SetName("Canceled hint");
        yield return new TestCaseData(Task.FromException(new InvalidOperationException("warm read failed"))).SetName("Faulted hint");
    }

    [TestCaseSource(nameof(NonSuccessfulHints))]
    public void DrainBalReadHint_swallows_non_successful_hint(Task hint)
    {
        BlockAccessListManager manager = CreateManager();
        manager.TrackBalReadHint(hint);

        Assert.DoesNotThrow(manager.DrainBalReadHint);
    }

    [Test]
    public void DrainBalReadHint_without_tracked_hint_is_noop()
    {
        BlockAccessListManager manager = CreateManager();

        Assert.DoesNotThrow(manager.DrainBalReadHint);
    }

    [Test]
    public void PrepareForProcessing_drops_hint_tracked_for_previous_block()
    {
        BlockAccessListManager manager = CreateManager();
        manager.TrackBalReadHint(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);

        manager.PrepareForProcessing(Build.A.Block.TestObject, Substitute.For<IReleaseSpec>(), ProcessingOptions.None);

        Task drain = Task.Run(manager.DrainBalReadHint);
        Assert.That(drain.Wait(DrainTimeout), Is.True, "a stale hint from the previous block must not be awaited");
    }
}
